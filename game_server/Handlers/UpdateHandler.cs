using network.interfaces;
using network.common;

namespace game_server.handlers
{
    public class UpdateHandler<T> : IUpdateHandler<T> where T : IMessagePackObject
    {
        private readonly Func<T, IPacket> _packetMaker;

        public UpdateHandler(Func<T, IPacket> packetMaker)
        {
            _packetMaker = packetMaker;
        }

        public IPacket MakePacket(T info) => _packetMaker(info);
    }


}