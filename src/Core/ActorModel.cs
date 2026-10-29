namespace IceyCoop.Core
{
    /// <summary>Authority-owned view of one actor. GameBridge maps native fields into it.</summary>
    public sealed class ActorModel
    {
        public byte PlayerId;
        public LifeState Life;
        public ConnectionState Connection;
        public ulong EntityId;
        public int Hp;
        public int Shield;

        public ActorModel()
        {
            Life = LifeState.Alive;
            Connection = ConnectionState.Disconnected;
        }
    }
}
