using System;
using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed partial class CampSession
    {
        public event Action<CampVoiceFrame> VoiceReceived;
        private readonly CampVoiceRouter _voiceRouter = new CampVoiceRouter();

        private void SetupVoiceTransport()
        {
            Transport.ServerVoice += ReceiveServerVoice;
            Transport.ClientVoice += ReceiveClientVoice;
            PlayerDisconnected += VoicePlayerDisconnected;
        }
        private void TeardownVoiceTransport()
        {
            if (Transport != null)
            { Transport.ServerVoice -= ReceiveServerVoice; Transport.ClientVoice -= ReceiveClientVoice; }
            PlayerDisconnected -= VoicePlayerDisconnected;
            _voiceRouter.Clear();
        }
        private void VoicePlayerDisconnected(string actor) => _voiceRouter.ResetActor(actor);
        private void ClearVoiceSession() => _voiceRouter.Clear();
        public bool SendVoice(CampVoiceFrame frame) => View?.Self != null && !_ending &&
            Transport.SendVoiceToHost(frame);

        private void ReceiveServerVoice(int peer, CampVoiceFrame frame)
        {
            // FishNet authentication alone is insufficient. Join must already have
            // verified this connection's durable GoldCamp identity and bound actor.
            if (_ending || Authority == null || !_actors.TryGetValue(peer, out var actor)) return;
            var sender = Authority.State.Players.Find(p => p.Id == actor);
            if (sender == null) return;
            var accepted = _voiceRouter.Accept(actor, frame, Time.realtimeSinceStartupAsDouble);
            if (accepted == null) return;
            foreach (var target in _actors)
            {
                if (target.Key == peer) continue;
                var receiver = Authority.State.Players.Find(p => p.Id == target.Value);
                if (receiver != null && CampVoiceProximity.CanHear(sender.Position, receiver.Position))
                    Transport.SendVoiceToClient(target.Key, accepted);
            }
        }
        private void ReceiveClientVoice(CampVoiceFrame frame)
        {
            if (View?.Self != null && !_ending && frame != null && frame.Data != null &&
                frame.Stream > 0 && frame.Sequence >= 0 && CampVoiceCodec.HasSupportedPacketFormat(frame.Data))
                VoiceReceived?.Invoke(frame);
        }
    }
}
