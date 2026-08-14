using System.Runtime.InteropServices;
using FiveMMcp.Native;

namespace FiveMMcp.Services;

/// <summary>
/// Synthesises keyboard and mouse input via SendInput.
/// Keys are sent as hardware scan codes, not virtual keys: GTA V reads keyboard
/// state through DirectInput/raw input, which ignores virtual-key-only injection.
/// </summary>
public sealed class InputService : IDisposable {
    private static readonly int InputSize = Marshal.SizeOf<User32.INPUT>();
    private readonly HashSet<ushort> _heldKeys = [];
    private readonly Lock _gate = new();

    public IReadOnlyCollection<ushort> HeldKeys {
        get { lock (_gate) { return _heldKeys.ToArray(); } }
    }

    private static ushort ScanCode(ushort vk) {
        var sc = (ushort)User32.MapVirtualKeyW(vk, User32.MAPVK_VK_TO_VSC_EX);
        // MAPVK_VK_TO_VSC_EX returns the E0 prefix in the high byte; SendInput
        // wants the low byte plus the extended flag.
        return (ushort)(sc & 0xFF);
    }

    private static User32.INPUT KeyInput(ushort vk, bool keyUp) {
        var flags = User32.KEYEVENTF_SCANCODE;
        if (keyUp) {
            flags |= User32.KEYEVENTF_KEYUP;
        }

        if (KeyMap.IsExtended(vk)) {
            flags |= User32.KEYEVENTF_EXTENDEDKEY;
        }

        return new User32.INPUT {
            type = User32.INPUT_KEYBOARD,
            u = new User32.INPUTUNION {
                ki = new User32.KEYBDINPUT { wVk = 0, wScan = ScanCode(vk), dwFlags = flags },
            },
        };
    }

    private static void Send(params User32.INPUT[] inputs) {
        var sent = User32.SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length) {
            throw new InvalidOperationException(
                $"SendInput delivered {sent}/{inputs.Length} events (Win32 error {Marshal.GetLastWin32Error()}). " +
                "This usually means the foreground window runs elevated - restart Claude Code as administrator.");
        }
    }

    public void KeyDown(ushort vk) {
        Send(KeyInput(vk, keyUp: false));
        lock (_gate) { _heldKeys.Add(vk); }
    }

    public void KeyUp(ushort vk) {
        Send(KeyInput(vk, keyUp: true));
        lock (_gate) { _heldKeys.Remove(vk); }
    }

    public void PressKey(ushort vk, int holdMs) {
        KeyDown(vk);
        Thread.Sleep(Math.Clamp(holdMs, 1, 5000));
        KeyUp(vk);
    }

    public void ReleaseAll() {
        ushort[] held;
        lock (_gate) { held = [.. _heldKeys]; }
        foreach (var vk in held) {
            try { KeyUp(vk); } catch { /* best effort on shutdown */ }
        }
    }

    /// <summary>
    /// Types literal text as Unicode key events. Suitable for the F8 console and
    /// chat boxes, which read characters through the normal Windows message loop.
    /// </summary>
    public void TypeText(string text, int perCharDelayMs = 8) {
        foreach (var ch in text) {
            if (ch == '\n') {
                PressKey(0x0D, 20);
                continue;
            }

            var down = new User32.INPUT {
                type = User32.INPUT_KEYBOARD,
                u = new User32.INPUTUNION {
                    ki = new User32.KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = User32.KEYEVENTF_UNICODE },
                },
            };
            var up = down;
            up.u.ki.dwFlags |= User32.KEYEVENTF_KEYUP;
            Send(down, up);

            if (perCharDelayMs > 0) {
                Thread.Sleep(perCharDelayMs);
            }
        }
    }

    /// <summary>Relative mouse motion - what the in-game camera reads via raw input.</summary>
    public void MoveRelative(int dx, int dy) {
        Send(new User32.INPUT {
            type = User32.INPUT_MOUSE,
            u = new User32.INPUTUNION {
                mi = new User32.MOUSEINPUT { dx = dx, dy = dy, dwFlags = User32.MOUSEEVENTF_MOVE },
            },
        });
    }

    /// <summary>Absolute cursor placement - for NUI/menu clicking, not camera control.</summary>
    public void MoveAbsolute(int screenX, int screenY) {
        User32.SetCursorPos(screenX, screenY);

        var vLeft = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN);
        var vTop = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        var vWidth = Math.Max(1, User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN));
        var vHeight = Math.Max(1, User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN));

        var nx = (int)Math.Round((screenX - vLeft) * 65535.0 / vWidth);
        var ny = (int)Math.Round((screenY - vTop) * 65535.0 / vHeight);

        Send(new User32.INPUT {
            type = User32.INPUT_MOUSE,
            u = new User32.INPUTUNION {
                mi = new User32.MOUSEINPUT {
                    dx = nx,
                    dy = ny,
                    dwFlags = User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE | User32.MOUSEEVENTF_VIRTUALDESK,
                },
            },
        });
    }

    public void Click(string button, int holdMs = 40) {
        var (down, up) = button.ToLowerInvariant() switch {
            "right" => (User32.MOUSEEVENTF_RIGHTDOWN, User32.MOUSEEVENTF_RIGHTUP),
            "middle" => (User32.MOUSEEVENTF_MIDDLEDOWN, User32.MOUSEEVENTF_MIDDLEUP),
            _ => (User32.MOUSEEVENTF_LEFTDOWN, User32.MOUSEEVENTF_LEFTUP),
        };

        Send(MouseFlag(down));
        Thread.Sleep(Math.Clamp(holdMs, 1, 5000));
        Send(MouseFlag(up));
    }

    public void Scroll(int clicks) {
        Send(new User32.INPUT {
            type = User32.INPUT_MOUSE,
            u = new User32.INPUTUNION {
                mi = new User32.MOUSEINPUT { mouseData = unchecked((uint)(clicks * 120)), dwFlags = User32.MOUSEEVENTF_WHEEL },
            },
        });
    }

    private static User32.INPUT MouseFlag(uint flag) => new() {
        type = User32.INPUT_MOUSE,
        u = new User32.INPUTUNION { mi = new User32.MOUSEINPUT { dwFlags = flag } },
    };

    public void Dispose() => ReleaseAll();
}
