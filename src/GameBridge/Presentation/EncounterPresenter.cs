namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// Client-side encounter presenter: mirrors authoritative encounter state for
    /// local UI/gate visuals (no authority).
    /// </summary>
    public sealed class EncounterPresenter
    {
        public string Key;
        public byte State;
        public uint Generation;
        public int AliveEnemies;

        public void Apply(IceyCoop.Protocol.EncounterStatePayload p)
        {
            Key = p.Key;
            State = p.State;
            Generation = p.Generation;
            AliveEnemies = p.AliveEnemies;
            BridgeContext.Info(string.Format("[t18] encounter state key={0} state={1} gen={2} alive={3}", Key, State, Generation, AliveEnemies));
        }
    }
}
