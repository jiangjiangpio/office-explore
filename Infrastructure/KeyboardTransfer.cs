namespace OfficeTouchBar.Infrastructure;

internal readonly record struct KeyboardPacket(ushort VirtualKey, ushort ScanCode, bool Extended, bool KeyUp);
internal readonly record struct KeyboardMessage(int Message, nint Key, nint Detail);

internal static class KeyboardTransfer
{
    internal static bool TransferBatch(IReadOnlyList<KeyboardMessage> messages, nint ink, nint target,
        Func<nint> foreground, Func<nint, bool> isSlideShow, Func<nint, nint, bool> restore,
        Func<ushort, bool> isDown, Func<KeyboardPacket[], uint> send)
    {
        if (messages.Count == 0 || foreground() != ink || !isSlideShow(target)) return false;
        if (!restore(target, ink) || foreground() != target) return false;
        var packets = new List<KeyboardPacket>();
        var held = new Dictionary<ushort, KeyboardPacket>();
        foreach (var message in messages)
        {
            foreach (var packet in Packets(message.Message, (ushort)message.Key, (long)message.Detail, true))
            {
                packets.Add(packet);
                if (packet.KeyUp) held.Remove(packet.VirtualKey);
                else held[packet.VirtualKey] = packet;
            }
        }
        foreach (var pair in held)
            if (!isDown(pair.Key)) packets.Add(pair.Value with { KeyUp = true });
        var sent = send(packets.ToArray());
        DiagnosticLog.Ink($"keyboard-batch messages={messages.Count} packets={packets.Count} sent={sent} target={target}");
        return true;
    }

    // WM_KEY*/WM_SYSKEY*: no navigation whitelist. The owner must be the
    // foreground ink window; once transferred, Windows owns subsequent input.
    internal static bool Transfer(int message, nint key, nint detail, nint ink, nint target,
        Func<nint> foreground, Func<nint, bool> isSlideShow, Func<nint, nint, bool> restore,
        Func<ushort, bool> isDown, Func<KeyboardPacket[], uint> send)
    {
        if (message is not (0x100 or 0x101 or 0x104 or 0x105) || key == 0x1b
            || foreground() != ink || !isSlideShow(target)) return false;
        if (!restore(target, ink) || foreground() != target) return false;
        var packets = Packets(message, (ushort)key, (long)detail, isDown((ushort)key));
        var sent = send(packets);
        DiagnosticLog.Write($"keyboard handoff target={target} key={key} message={message:X} count={packets.Length} sent={sent}");
        // The message belongs to the old ink queue and must not be processed a
        // second time by WPF after focus has moved, including injection failure.
        return true;
    }

    internal static KeyboardPacket[] Packets(int message, ushort key, long detail, bool stillDown)
    {
        var up = message is 0x101 or 0x105;
        var packet = new KeyboardPacket(key, (ushort)((detail >> 16) & 0xff),
            (detail & (1L << 24)) != 0, up);
        var packets = Enumerable.Repeat(packet, up ? 1 : Math.Max(1, (int)(detail & 0xffff))).ToList();
        // Fast presenter taps may already be physically released before the
        // queued down is handled. Reconstruct that release to avoid stuck keys.
        if (!up && !stillDown) packets.Add(packet with { KeyUp = true });
        return packets.ToArray();
    }
}
