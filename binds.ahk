; older version of the software, does mostly the same thing apart from window maximiser and knob control

#Requires AutoHotkey v2.0



; emulate mouse actions
!z::
{
    Send("{AppsKey}")
}

!c::Click()


; escape
<!x:: {
    SendLevel 1
    SendEvent("{Esc}")
}



; Line navigation
!]::Send("{End}")        ; Alt+]        -> jump to end of line
![::Send("{Home}")       ; Alt+[        -> jump to start of line
+!]::Send("+{End}")      ; Shift+Alt+]  -> select to end of line
+![::Send("+{Home}")     ; Shift+Alt+[  -> select to start of line

; Alt+O: go to end of line, open a new line below, cursor at its start.
; Uses Shift+Enter instead of Enter so chat/textarea apps insert a newline
; instead of submitting the message.
InsertLineBelow() {
    Send("{End}{Shift down}{Enter}{Shift up}")
}
!o::InsertLineBelow()    ; Alt+O
+!o::InsertLineBelow()   ; Shift+Alt+O (same behavior)


; Remap PgUp / PgDn to Win + Ctrl + Right / Left
PgUp::Send "^#{Left}"
PgDn::Send "^#{Right}"


; Scroll 
scrollSteps := 3     ; Number of scroll steps per key press
scrollDelay := 10    ; Delay between scroll steps (in milliseconds)

; Vertical scrolling
!j::Scroll("Down")   ; Alt + j scrolls down
!k::Scroll("Up")     ; Alt + k scrolls up

; Horizontal scrolling
!h::Scroll("Left")   ; Alt + h scrolls left
!l::Scroll("Right")  ; Alt + l scrolls right

Scroll(direction) {
    global scrollSteps, scrollDelay
    Loop scrollSteps {
        Send("{Wheel" direction "}")
        Sleep(scrollDelay)
    }
}



; jump mouse to active CenterMouseOnActiveWindow

#SingleInstance Force


; Configuration
hoverInterval  := 700              ; Interval (ms) between hover checks
prevUnderMouse := ""              
prevActive     := WinExist("A")    ; Track the window active before Alt+Tab

; Alt-Up hotkeys—center only on Alt+Tab release
~LAlt Up::AltReleased()
~RAlt Up::AltReleased()

AltReleased() {
    global prevActive
    StopTrackpointGesture()        ; cut scrolling immediately, don't wait for next raw-input event
    Sleep 50                       ; Allow Windows to settle focus
    hwndNew := WinExist("A")
    if (hwndNew && hwndNew != prevActive
        && !MouseInsideWindow(hwndNew)
    ) {
        CenterMouseOnWindow(hwndNew)
    }
    prevActive := hwndNew
}

; Hover-to-focus (no centering)
SetTimer(HoverFocus, hoverInterval)
HoverFocus() {
    global prevUnderMouse
    if GetKeyState("Alt","P")       ; Suspend hover during Alt sequences
        return

    MouseGetPos(, , &hwnd)
    if (hwnd 
        && hwnd != prevUnderMouse 
        && IsRealWindow(hwnd)
    ) {
        prevUnderMouse := hwnd
        WinActivate("ahk_id " hwnd)
    }
}

; Helpers

IsRealWindow(hwnd) {
    style := WinGetStyle("ahk_id " hwnd)

    WS_VISIBLE          := 0x10000000
    WS_OVERLAPPEDWINDOW := 0x00CF0000
    WS_CHILD            := 0x40000000
    WS_POPUP            := 0x80000000

    return (style & WS_VISIBLE)
        && (style & WS_OVERLAPPEDWINDOW)
        && !(style & WS_CHILD)
        && !(style & WS_POPUP)
}

MouseInsideWindow(hwnd) {
    WinGetPos(&x,&y,&w,&h, "ahk_id " hwnd)
    MouseGetPos(&mx,&my)
    return (mx >= x && mx <= x + w && my >= y && my <= y + h)
}

CenterMouseOnWindow(hwnd) {
    WinGetPos(&x,&y,&w,&h, "ahk_id " hwnd)
    if (w > 0 && h > 0) {
        cx := x + (w // 2)
        cy := y + (h // 2)
        DllCall("SetCursorPos", "Int", cx, "Int", cy)
    }
}


; ============================================================
; Alt + TrackPoint => drag-scroll (like holding the middle button)
;
; The OEM middle-button drag-scroll is implemented below Windows
; (ELAN driver / firmware), so a synthetic middle-click can't trigger
; it. Instead: read the TrackPoint's raw relative motion via Raw Input,
; and while Alt is held, convert it into wheel events; the cursor is
; pinned back to its start-of-gesture position after each event so it
; doesn't drift while scrolling.
;
; Note: this machine's TrackPoint is "TrackPoint Device" (ACPI\LEN0325,
; ELAN). If DeviceMatch below fails to find it, check Device Manager
; for the real hardware ID and update DeviceMatch.
; Doesn't affect elevated foreground windows (UIPI) unless this script
; itself runs elevated.
; ============================================================

DeviceMatch := "LEN0325"        ; substring of the TrackPoint's raw-input device path
ScrollThreshold := 8            ; scroll-units accumulated per wheel tick (lower = faster/twitchier)
CurveScale := 4                 ; raw per-sample deflection that maps 1:1 to scroll-units (tune to knob sensitivity)
CurveExponent := 1.6            ; >1 = slower start, ramps up faster as deflection grows; 1 = linear
MaxTicksPerEvent := 3           ; ponytail: hard cap on wheel ticks a single raw-input sample can queue. Without this, CurveExponent's polynomial growth on a hard/fast push (a held TrackPoint keeps re-reporting ~the same big delta every poll, ~100+/sec) fires dozens of Send() calls per event — the receiving app then spends the next second animating/smooth-scrolling through that queued burst, which looks like "still scrolling after I let go of Alt". Raise only if taps of the knob feel throttled.

HeaderSize := A_PtrSize = 8 ? 24 : 16   ; sizeof(RAWINPUTHEADER)
TPDevice := 0
AccumX := 0
AccumY := 0
InGesture := false
FrozenX := 0
FrozenY := 0

ParseRawMouse(buf) {
    global HeaderSize
    if NumGet(buf, 0, "UInt") != 0   ; RIM_TYPEMOUSE
        return false
    return { hDevice: NumGet(buf, 8, "Ptr")
           , flags:   NumGet(buf, HeaderSize, "UShort")
           , dx:      NumGet(buf, HeaderSize + 12, "Int")
           , dy:      NumGet(buf, HeaderSize + 16, "Int") }
}

SelfTestParseRawMouse() {
    global HeaderSize
    buf := Buffer(HeaderSize + 24, 0)
    NumPut("UInt", 0, buf, 0)
    NumPut("Ptr", 4321, buf, 8)
    NumPut("UShort", 0, buf, HeaderSize)
    NumPut("Int", 5, buf, HeaderSize + 12)
    NumPut("Int", -7, buf, HeaderSize + 16)
    r := ParseRawMouse(buf)
    if !(r && r.hDevice = 4321 && r.dx = 5 && r.dy = -7)
        throw Error("Alt+TrackPoint scroll: RAWMOUSE offsets are wrong for A_PtrSize=" A_PtrSize)
}

FindTrackpointDevice() {
    global DeviceMatch
    entrySize := A_PtrSize = 8 ? 16 : 8
    count := 0
    DllCall("GetRawInputDeviceList", "Ptr", 0, "UInt*", &count, "UInt", entrySize)
    if !count
        return 0
    buf := Buffer(entrySize * count, 0)
    got := DllCall("GetRawInputDeviceList", "Ptr", buf, "UInt*", &count, "UInt", entrySize)
    if got = 0xFFFFFFFF
        return 0
    Loop got {
        offset := (A_Index - 1) * entrySize
        hDevice := NumGet(buf, offset, "Ptr")
        if NumGet(buf, offset + A_PtrSize, "UInt") != 0   ; RIM_TYPEMOUSE
            continue
        nameSize := 0
        DllCall("GetRawInputDeviceInfoW", "Ptr", hDevice, "UInt", 0x20000007, "Ptr", 0, "UInt*", &nameSize)
        if !nameSize
            continue
        nameBuf := Buffer(nameSize * 2, 0)
        DllCall("GetRawInputDeviceInfoW", "Ptr", hDevice, "UInt", 0x20000007, "Ptr", nameBuf, "UInt*", &nameSize)
        if InStr(StrGet(nameBuf, "UTF-16"), DeviceMatch)
            return hDevice
    }
    return 0
}

CurveDelta(d) {
    global CurveScale, CurveExponent, ScrollThreshold, MaxTicksPerEvent
    if d = 0
        return 0
    mag := (Abs(d) / CurveScale) ** CurveExponent * CurveScale
    mag := Min(mag, ScrollThreshold * MaxTicksPerEvent)   ; bound the burst a single sample can cause
    return d > 0 ? mag : -mag
}

AccumulateAxis(accum, rawDelta) {
    curved := CurveDelta(rawDelta)
    if curved = 0
        return accum
    if (accum > 0 && curved < 0) || (accum < 0 && curved > 0)
        return curved   ; direction reversed - drop stale momentum instead of fighting it through the curve
    return accum + curved
}

StopTrackpointGesture() {
    global AccumX, AccumY, InGesture
    AccumX := 0, AccumY := 0, InGesture := false
}

TrackpointWatchdog() {
    global InGesture
    if InGesture && !GetKeyState("Alt", "P")   ; backstop in case the Alt-up hotkey race is ever lost
        StopTrackpointGesture()
}

RegisterTrackpointRawInput() {
    global TPDevice
    TPDevice := FindTrackpointDevice()
    if !TPDevice {
        TrayTip("Alt+TrackPoint scroll disabled", "TrackPoint raw-input device not found", 0x30)
        return false
    }
    ridSize := A_PtrSize = 8 ? 16 : 12
    rid := Buffer(ridSize, 0)
    NumPut("UShort", 0x01, rid, 0)      ; usUsagePage = Generic Desktop
    NumPut("UShort", 0x02, rid, 2)      ; usUsage = Mouse
    NumPut("UInt", 0x100, rid, 4)       ; dwFlags = RIDEV_INPUTSINK
    NumPut("Ptr", A_ScriptHwnd, rid, 8) ; hwndTarget
    return DllCall("RegisterRawInputDevices", "Ptr", rid, "UInt", 1, "UInt", ridSize)
}

OnRawInput(wParam, lParam, msg, hwnd) {
    global TPDevice, AccumX, AccumY, ScrollThreshold, HeaderSize, InGesture, FrozenX, FrozenY
    if !GetKeyState("Alt", "P") {
        StopTrackpointGesture()
        return
    }
    size := 0
    DllCall("GetRawInputData", "Ptr", lParam, "UInt", 0x10000003, "Ptr", 0, "UInt*", &size, "UInt", HeaderSize)
    if !size
        return
    buf := Buffer(size, 0)
    if DllCall("GetRawInputData", "Ptr", lParam, "UInt", 0x10000003, "Ptr", buf, "UInt*", &size, "UInt", HeaderSize) != size
        return
    r := ParseRawMouse(buf)
    if !r || r.hDevice != TPDevice || (r.flags & 1)   ; wrong device, or absolute-mode report
        return
    if !InGesture {
        InGesture := true
        pt := Buffer(8)
        DllCall("GetCursorPos", "Ptr", pt)
        FrozenX := NumGet(pt, 0, "Int")
        FrozenY := NumGet(pt, 4, "Int")
    }
    AccumX := AccumulateAxis(AccumX, r.dx)
    AccumY := AccumulateAxis(AccumY, r.dy)
    while Abs(AccumY) >= ScrollThreshold {
        Send(AccumY > 0 ? "{WheelDown}" : "{WheelUp}")
        AccumY += (AccumY > 0 ? -ScrollThreshold : ScrollThreshold)
    }
    while Abs(AccumX) >= ScrollThreshold {
        Send(AccumX > 0 ? "{WheelRight}" : "{WheelLeft}")
        AccumX += (AccumX > 0 ? -ScrollThreshold : ScrollThreshold)
    }
    DllCall("SetCursorPos", "Int", FrozenX, "Int", FrozenY)   ; undo whatever the OS just moved
}

SelfTestParseRawMouse()
OnMessage(0x00FF, OnRawInput)
RegisterTrackpointRawInput()
SetTimer(TrackpointWatchdog, 30)