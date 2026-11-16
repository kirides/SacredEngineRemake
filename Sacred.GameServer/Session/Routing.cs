namespace Sacred.GameServer.Session;

/// <summary>
/// Recipient selection of GameServer's forwarder <c>FUN_004d3ea0(except, data, size, opcode, origin, mode, objectRef)</c>.
/// </summary>
public enum RouteMode : byte
{
    None = 0,
    /// <summary>1: every other connected player.</summary>
    Connected = 1,
    /// <summary>2: every other player that has entered the world (+0x75 set by 0x17/0x18).</summary>
    InGame = 2,
    /// <summary>3: in game and the referenced object's sector is visible to the player.</summary>
    SectorVisible = 3,
    /// <summary>5: same party/team as the origin.</summary>
    Party = 5,
    /// <summary>6: players in the referenced object's visibility mask (+0x3A).</summary>
    ObjectVisible = 6,
    /// <summary>7: in game and in the object's sector.</summary>
    SameSector = 7,
    /// <summary>8: sector visible and in the object's visibility mask.</summary>
    SectorAndObject = 8,
}

/// <summary>
/// Pure forwards of the original dispatcher <c>FUN_004c3f20</c> (switch on opcode-3, index table 0x4CC9C4, jump table 0x4CC6E0):
/// the cases whose only network effect is re-sending the incoming message through <c>FUN_004d3ea0</c>. Extracted from the call
/// sites' pushed arguments (see README "Routing"). Opcodes that are not listed here and not handled explicitly are dropped by the
/// original too (its default case only logs).
/// </summary>
public static class Routing
{
    public const int OpcodeLimit = 0x200;
    private static readonly RouteMode[] Modes = new RouteMode[OpcodeLimit];
    private static readonly bool[] IncludesSender = new bool[OpcodeLimit];

    static Routing()
    {
        Set(RouteMode.Connected, 0x151, 0x138, 0x13B);
        Set(RouteMode.InGame, 0x15E, 0x160, 0x185, 0x186, 0x1B0, 0x1B6, 0x1B7, 0x1BA, 0x1BB, 0x1BF, 0x1C1, 0x1C6, 0x1C8, 0x1CC,
            0x1CF, 0x1D4, 0x1D5, 0x18E, 0x18F, 0x190, 0x191, 0x192, 0x193, 0x194, 0x195,
            0x125, 0x149, 0x14A, 0x14D, 0x14E, 0x181, 0x182, 0x184, 0x189, 0x196);
        Set(RouteMode.SectorVisible, 0x111, 0x112, 0x18C, 0x18D, 0x199, 0x19B);
        Set(RouteMode.Party, 0x145);
        Set(RouteMode.ObjectVisible, 0x11A, 0x11B, 0x14B, 0x169, 0x16B, 0x16C, 0x16D, 0x16E, 0x16F, 0x177, 0x1A6, 0x1A9);
        Set(RouteMode.SectorAndObject, 0x113, 0x11F, 0x141, 0x161, 0x163, 0x183);
        Set(RouteMode.SectorAndObject, 0x11E);
        IncludesSender[0x11E] = true; // pushed except = 0x11 (nobody excluded)
    }

    private static void Set(RouteMode mode, params ushort[] opcodes)
    {
        foreach (var opcode in opcodes) Modes[opcode] = mode;
    }

    public static RouteMode Mode(ushort opcode) => opcode < OpcodeLimit ? Modes[opcode] : RouteMode.None;
    public static bool IncludeSender(ushort opcode) => opcode < OpcodeLimit && IncludesSender[opcode];
}
