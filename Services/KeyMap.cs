namespace FiveMMcp.Services;

/// <summary>Maps friendly key names to Windows virtual-key codes.</summary>
public static class KeyMap {
    private static readonly Dictionary<string, ushort> Keys = new(StringComparer.OrdinalIgnoreCase) {
        ["backspace"] = 0x08,
        ["tab"] = 0x09,
        ["enter"] = 0x0D,
        ["return"] = 0x0D,
        ["shift"] = 0xA0,
        ["lshift"] = 0xA0,
        ["rshift"] = 0xA1,
        ["ctrl"] = 0xA2,
        ["lctrl"] = 0xA2,
        ["rctrl"] = 0xA3,
        ["control"] = 0xA2,
        ["alt"] = 0xA4,
        ["lalt"] = 0xA4,
        ["ralt"] = 0xA5,
        ["pause"] = 0x13,
        ["capslock"] = 0x14,
        ["esc"] = 0x1B,
        ["escape"] = 0x1B,
        ["space"] = 0x20,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["end"] = 0x23,
        ["home"] = 0x24,
        ["left"] = 0x25,
        ["up"] = 0x26,
        ["right"] = 0x27,
        ["down"] = 0x28,
        ["insert"] = 0x2D,
        ["delete"] = 0x2E,
        ["numpad0"] = 0x60,
        ["numpad1"] = 0x61,
        ["numpad2"] = 0x62,
        ["numpad3"] = 0x63,
        ["numpad4"] = 0x64,
        ["numpad5"] = 0x65,
        ["numpad6"] = 0x66,
        ["numpad7"] = 0x67,
        ["numpad8"] = 0x68,
        ["numpad9"] = 0x69,
        ["multiply"] = 0x6A,
        ["add"] = 0x6B,
        ["subtract"] = 0x6D,
        ["decimal"] = 0x6E,
        ["divide"] = 0x6F,
        ["tilde"] = 0xC0,
        ["grave"] = 0xC0,
        ["backtick"] = 0xC0,
        ["minus"] = 0xBD,
        ["equals"] = 0xBB,
        ["lbracket"] = 0xDB,
        ["rbracket"] = 0xDD,
        ["backslash"] = 0xDC,
        ["semicolon"] = 0xBA,
        ["quote"] = 0xDE,
        ["comma"] = 0xBC,
        ["period"] = 0xBE,
        ["slash"] = 0xBF,
    };

    /// <summary>Keys that must be sent with the extended-key flag (E0 prefix).</summary>
    private static readonly HashSet<ushort> Extended =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // page/home/end/arrows
        0x2D, 0x2E, // insert, delete
        0xA3, 0xA5, // right ctrl, right alt
        0x6F, // numpad divide
    ];

    public static bool TryResolve(string name, out ushort vk) {
        name = name.Trim();

        if (name.Length == 1) {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') {
                vk = c;
                return true;
            }
        }

        // F1-F24
        if (name.Length is 2 or 3 &&
            (name[0] == 'F' || name[0] == 'f') &&
            int.TryParse(name[1..], out var fn) &&
            fn is >= 1 and <= 24) {
            vk = (ushort)(0x70 + fn - 1);
            return true;
        }

        return Keys.TryGetValue(name, out vk);
    }

    public static bool IsExtended(ushort vk) => Extended.Contains(vk);

    public static IEnumerable<string> KnownNames => Keys.Keys;
}
