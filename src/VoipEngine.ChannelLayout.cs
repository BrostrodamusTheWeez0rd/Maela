using System;
using System.Collections.Generic;
using System.IO;

namespace Maela
{
    /// <summary>Where the host has put a channel around the room in the Dynamic layout.</summary>
    internal sealed class ChannelLayout
    {
        public double Angle;  // Radians round the room, 0 to the right.
        public double Reach;  // 1 is the usual distance from the room.
        public double Size;   // 1 is the usual size.
    }

    /// <summary>
    /// Channel layout: the host can move channels round the room and resize them, and everyone
    /// sees it live. It travels in the peer list (section 7, after everything older versions know)
    /// as four small numbers per channel, so a hostile host can do no more than move balls about.
    /// </summary>
    internal sealed partial class VoipEngine
    {
        public const double MinChannelReach = 0.55, MaxChannelReach = 1.7;
        public const double MinChannelSize = 0.6, MaxChannelSize = 1.8;
        private const byte PeerListSectionChannelLayout = 7;
        private const double LayoutBroadcastSeconds = 0.08;

        private readonly Dictionary<int, ChannelLayout> _channelLayouts = new Dictionary<int, ChannelLayout>(); // Under _stateLock.
        private int _channelLayoutsVersion;
        private bool _layoutBroadcastPending;
        private DateTime _lastLayoutBroadcastUtc = DateTime.MinValue;

        /// <summary>Increments whenever the layout changes. Cheap to poll from the UI.</summary>
        public int ChannelLayoutsVersion { get { lock (_stateLock) return _channelLayoutsVersion; } }

        /// <summary>A channel's layout, or null where it sits in its usual place.</summary>
        public ChannelLayout GetChannelLayout(int channelId)
        {
            lock (_stateLock)
            {
                ChannelLayout layout;
                if (!_channelLayouts.TryGetValue(channelId, out layout))
                    return null;
                ChannelLayout copy = new ChannelLayout();
                copy.Angle = layout.Angle;
                copy.Reach = layout.Reach;
                copy.Size = layout.Size;
                return copy;
            }
        }

        /// <summary>Host only: puts a channel somewhere new. Sent to everyone a few times a second while it moves.</summary>
        public void SetChannelLayout(int channelId, double angle, double reach, double size)
        {
            if (!_isHost)
                return;
            bool sendNow;
            lock (_stateLock)
            {
                if (!_channels.ContainsKey(channelId))
                    return;
                ChannelLayout layout = new ChannelLayout();
                // Exactly as the wire carries it, so the host sees what everyone else does.
                layout.Angle = UnpackAngle(PackAngle(angle));
                layout.Reach = Math.Round(Clamp(reach, MinChannelReach, MaxChannelReach) * 100) / 100;
                layout.Size = Math.Round(Clamp(size, MinChannelSize, MaxChannelSize) * 100) / 100;
                _channelLayouts[channelId] = layout;
                _channelLayoutsVersion++;
                sendNow = (DateTime.UtcNow - _lastLayoutBroadcastUtc).TotalSeconds >= LayoutBroadcastSeconds;
                _layoutBroadcastPending = !sendNow;
                if (sendNow)
                    _lastLayoutBroadcastUtc = DateTime.UtcNow;
            }
            if (sendNow)
                BroadcastPeerList();
        }

        /// <summary>Host maintenance: sends a layout change that came too soon after the last one.</summary>
        private void HostLayoutMaintenance()
        {
            lock (_stateLock)
            {
                if (!_layoutBroadcastPending || (DateTime.UtcNow - _lastLayoutBroadcastUtc).TotalSeconds < LayoutBroadcastSeconds)
                    return;
                _layoutBroadcastPending = false;
                _lastLayoutBroadcastUtc = DateTime.UtcNow;
            }
            BroadcastPeerList();
        }

        private static double Clamp(double value, double min, double max)
        {
            if (Double.IsNaN(value))
                return 1;
            return Math.Max(min, Math.Min(max, value));
        }

        private static ushort PackAngle(double angle)
        {
            double turn = angle / (2 * Math.PI);
            turn -= Math.Floor(turn);
            return (ushort)Math.Min(65535, Math.Round(turn * 65536));
        }

        private static double UnpackAngle(ushort packed)
        {
            return packed / 65536.0 * 2 * Math.PI;
        }

        /// <summary>Host: [u8 count] then per channel [i32 id][u16 angle][u8 reach %][u8 size %]. _stateLock held.</summary>
        private void WriteChannelLayoutSection(BinaryWriter writer)
        {
            List<KeyValuePair<int, ChannelLayout>> list = new List<KeyValuePair<int, ChannelLayout>>();
            foreach (KeyValuePair<int, ChannelLayout> pair in _channelLayouts)
            {
                if (_channels.ContainsKey(pair.Key))
                    list.Add(pair);
            }
            if (list.Count == 0)
                return;
            writer.Write(PeerListSectionChannelLayout);
            writer.Write((byte)Math.Min(255, list.Count));
            for (int i = 0; i < list.Count && i < 255; i++)
            {
                writer.Write(list[i].Key);
                writer.Write(PackAngle(list[i].Value.Angle));
                writer.Write((byte)Math.Round(list[i].Value.Reach * 100));
                writer.Write((byte)Math.Round(list[i].Value.Size * 100));
            }
        }

        /// <summary>Client: reads the layout section, clamping every value. Returns the new offset, or -1 if malformed.</summary>
        private static int ReadChannelLayoutSection(byte[] payload, int offset, Dictionary<int, ChannelLayout> into)
        {
            if (offset >= payload.Length)
                return -1;
            int count = payload[offset++];
            if (offset + count * 8 > payload.Length)
                return -1;
            for (int i = 0; i < count; i++)
            {
                ChannelLayout layout = new ChannelLayout();
                int id = BitConverter.ToInt32(payload, offset);
                layout.Angle = UnpackAngle(BitConverter.ToUInt16(payload, offset + 4));
                layout.Reach = Clamp(payload[offset + 6] / 100.0, MinChannelReach, MaxChannelReach);
                layout.Size = Clamp(payload[offset + 7] / 100.0, MinChannelSize, MaxChannelSize);
                into[id] = layout;
                offset += 8;
            }
            return offset;
        }

        /// <summary>Client: replace the layout with what the host sent. _stateLock held.</summary>
        private void ApplyChannelLayoutsLocked(Dictionary<int, ChannelLayout> parsed)
        {
            bool changed = parsed.Count != _channelLayouts.Count;
            if (!changed)
            {
                foreach (KeyValuePair<int, ChannelLayout> pair in parsed)
                {
                    ChannelLayout old;
                    if (!_channelLayouts.TryGetValue(pair.Key, out old) || old.Angle != pair.Value.Angle || old.Reach != pair.Value.Reach || old.Size != pair.Value.Size)
                    {
                        changed = true;
                        break;
                    }
                }
            }
            if (!changed)
                return;
            _channelLayouts.Clear();
            foreach (KeyValuePair<int, ChannelLayout> pair in parsed)
            {
                if (_channels.ContainsKey(pair.Key))
                    _channelLayouts[pair.Key] = pair.Value;
            }
            _channelLayoutsVersion++;
        }
    }
}
