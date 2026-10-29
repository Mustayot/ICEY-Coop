using System.Collections.Generic;

namespace IceyCoop.Core
{
    public enum RemoteActionKind
    {
        Attack,
        HeavyAttack,
        Charge,
        ChargeRelease,
        AtkCharge,
        AtkChargeRelease,
        Jump,
        Dash,
        UpRising,
        HitGround,
        Skill,
        SkillAlt,
        Defence,
        Execute,
        Interaction
    }

    /// <summary>
    /// Converts sampled remote held/edge bitsets into native action intents.
    /// Timed light and heavy presses are tracked per remote actor tick so a
    /// release edge without a known down state can never synthesize a click.
    /// </summary>
    public sealed class RemoteActionRouter
    {
        private const uint ShortPressLimitMs = 300;

        private bool _attackActive;
        private uint _attackStartedAt;
        private bool _heavyActive;
        private uint _heavyStartedAt;
        private bool _suppressLightUntilAttackRelease;
        private bool _atkChargeActive;
        private uint _atkChargeStartedAt;

        public bool HasHeavyPress { get { return _heavyActive; } }

        /// <summary>True while an ordinary attack charge is running; the adapter
        /// cancels it exactly once on reset or timeout.</summary>
        public bool HasAtkCharge { get { return _atkChargeActive; } }

        public List<RemoteActionKind> Update(ushort held, ushort pressed, ushort released, uint nowMs)
        {
            List<RemoteActionKind> actions = new List<RemoteActionKind>();

            bool attackHeld = IsSet(held, InputActions.Attack);
            bool attackPressed = IsSet(pressed, InputActions.Attack);
            bool attackReleased = IsSet(released, InputActions.Attack);
            bool heavyHeld = IsSet(held, InputActions.CirtAttack);
            bool heavyPressed = IsSet(pressed, InputActions.CirtAttack);
            bool heavyReleased = IsSet(released, InputActions.CirtAttack);

            bool chargeHeld = IsSet(held, InputActions.Charging);
            bool chargePressed = IsSet(pressed, InputActions.Charging);
            bool chargeReleased = IsSet(released, InputActions.Charging);

            if (chargePressed && !_atkChargeActive)
            {
                _atkChargeActive = true;
                _atkChargeStartedAt = nowMs;
            }
            else if (chargeHeld && !_atkChargeActive)
            {
                // Same rule as the heavy key: a held state without a press edge
                // starts the timer but can never synthesize a release by itself.
                _atkChargeActive = true;
                _atkChargeStartedAt = nowMs;
            }
            else if (!chargeHeld && !chargePressed && !chargeReleased)
            {
                _atkChargeActive = false;
            }

            uint chargeElapsed = _atkChargeActive ? Elapsed(nowMs, _atkChargeStartedAt) : 0u;
            bool chargeReachedThreshold = _atkChargeActive && chargeElapsed >= ShortPressLimitMs;
            // A release before the threshold is an ordinary short tap, not a charge.
            bool chargeReleasing = chargeReachedThreshold && (chargeReleased || attackReleased);
            bool chargeWasActive = chargeReleasing;

            bool dashPressed = IsSet(pressed, InputActions.Dash);
            bool upRisingPressed = IsSet(pressed, InputActions.UpRising);
            bool hitGroundPressed = IsSet(pressed, InputActions.HitGround);

            if (IsSet(pressed, InputActions.Jump))
            {
                actions.Add(RemoteActionKind.Jump);
            }
            if (IsSet(pressed, InputActions.Skill))
            {
                actions.Add(RemoteActionKind.Skill);
            }
            if (IsSet(pressed, InputActions.SkillAlt))
            {
                actions.Add(RemoteActionKind.SkillAlt);
            }
            if (IsSet(pressed, InputActions.Defence))
            {
                actions.Add(RemoteActionKind.Defence);
            }
            if (IsSet(pressed, InputActions.Execute))
            {
                actions.Add(RemoteActionKind.Execute);
            }
            if (IsSet(pressed, InputActions.Interaction))
            {
                actions.Add(RemoteActionKind.Interaction);
            }

            if (dashPressed)
            {
                actions.Add(RemoteActionKind.Dash);
            }
            else if (upRisingPressed)
            {
                actions.Add(RemoteActionKind.UpRising);
            }
            else if (hitGroundPressed)
            {
                actions.Add(RemoteActionKind.HitGround);
            }

            bool hasSpecialPress = dashPressed || upRisingPressed || hitGroundPressed;
            if (upRisingPressed && (_attackActive || attackPressed || attackHeld))
            {
                _suppressLightUntilAttackRelease = true;
            }
            bool suppressAttack = hasSpecialPress || _suppressLightUntilAttackRelease;

            if (attackPressed && !_attackActive)
            {
                _attackActive = true;
                _attackStartedAt = nowMs;
            }
            else if (attackHeld && !_attackActive)
            {
                // The press edge may have been lost in transit. Held state is
                // enough to begin timing, but never enough to create a release
                // click unless a later release is observed.
                _attackActive = true;
                _attackStartedAt = nowMs;
            }

            if (attackReleased)
            {
                if (_attackActive && !suppressAttack && !chargeWasActive
                    && Elapsed(nowMs, _attackStartedAt) < ShortPressLimitMs)
                {
                    actions.Add(RemoteActionKind.Attack);
                }
                _attackActive = false;
                _suppressLightUntilAttackRelease = false;
            }
            else if (!attackHeld && !attackPressed)
            {
                // A missing release must not leave an old timer that can turn a
                // later unrelated edge into an attack.
                _attackActive = false;
                _suppressLightUntilAttackRelease = false;
            }

            if (heavyPressed && !_heavyActive)
            {
                _heavyActive = true;
                _heavyStartedAt = nowMs;
            }
            else if (heavyHeld && !_heavyActive)
            {
                _heavyActive = true;
                _heavyStartedAt = nowMs;
            }

            if (heavyReleased)
            {
                if (_heavyActive)
                {
                    uint elapsed = Elapsed(nowMs, _heavyStartedAt);
                    if (elapsed < ShortPressLimitMs)
                    {
                        actions.Add(RemoteActionKind.HeavyAttack);
                    }
                    actions.Add(RemoteActionKind.ChargeRelease);
                }
                _heavyActive = false;
            }
            else if (_heavyActive && heavyHeld)
            {
                if (Elapsed(nowMs, _heavyStartedAt) >= ShortPressLimitMs)
                {
                    actions.Add(RemoteActionKind.Charge);
                }
            }
            else if (!heavyHeld && !heavyPressed)
            {
                _heavyActive = false;
            }

            if (chargeReleasing)
            {
                actions.Add(RemoteActionKind.AtkChargeRelease);
                _atkChargeActive = false;
            }
            else if (chargeReleased || attackReleased)
            {
                // Let go before the charge threshold: nothing was charged.
                _atkChargeActive = false;
            }
            else if (chargeReachedThreshold && chargeHeld)
            {
                actions.Add(RemoteActionKind.AtkCharge);
            }

            return actions;
        }

        public void Reset()
        {
            _attackActive = false;
            _attackStartedAt = 0;
            _heavyActive = false;
            _heavyStartedAt = 0;
            _suppressLightUntilAttackRelease = false;
            _atkChargeActive = false;
            _atkChargeStartedAt = 0;
        }

        private static bool IsSet(ushort bits, int action)
        {
            return (bits & (1 << action)) != 0;
        }

        private static uint Elapsed(uint nowMs, uint startedAt)
        {
            return unchecked(nowMs - startedAt);
        }
    }
}
