namespace IceyCoop.Core
{
    /// <summary>
    /// Pure composition rules for the two skills.
    ///
    /// Vanilla builds them from several sources at once (GameWorld.Joystick.UpdateGameInput
    /// IL): <c>Game.BladeStorm._isPressed = (RawKey.L1.Pressed &amp;&amp; RawKey.Square.Pressed)
    /// || Input.GetKey(键位表15)</c>, and ShadeAtk the same with Triangle / 16. On a client
    /// the RawKey path is dead (RawKey.Pressed is gated by Core.Input.JoystickIsOpen, which
    /// the client keeps closed) and the key map may be missing its skill entries
    /// (GetCode then yields KeyCode.None), so both vanilla sources can fail at once.
    ///
    /// Keeping the decision here - instead of inline in the sampler - means the truth table
    /// is testable without a game build.
    /// </summary>
    public static class InputComposition
    {
        /// <summary>The first skill (BladeStorm). Any usable source wins.</summary>
        public static bool Skill(bool vanillaButton, bool modifierHeld, bool faceButton, bool defaultKey)
        {
            return vanillaButton || (modifierHeld && faceButton) || defaultKey;
        }

        /// <summary>The second skill (ShadeAtk); same shape, different face button/key.</summary>
        public static bool SkillAlt(bool vanillaButton, bool modifierHeld, bool faceButton, bool defaultKey)
        {
            return Skill(vanillaButton, modifierHeld, faceButton, defaultKey);
        }

        /// <summary>True when the game's own composite is unusable and the sampler had to
        /// fall back - logged so a remapped-but-broken key map stays visible.</summary>
        public static bool NeededFallback(bool vanillaButton)
        {
            return !vanillaButton;
        }
    }
}
