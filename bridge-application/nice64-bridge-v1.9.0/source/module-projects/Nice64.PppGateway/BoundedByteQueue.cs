namespace PicoSwitchController;

internal sealed class BoundedByteQueue
{
    private readonly byte[] _buffer;
    private int _head;
    private int _count;

    public BoundedByteQueue(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new byte[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;
    public int HighWaterMark { get; private set; }

    public bool TryEnqueue(ReadOnlySpan<byte> data)
    {
        if (data.Length > Capacity - _count) return false;
        int tail = (_head + _count) % Capacity;
        int first = Math.Min(data.Length, Capacity - tail);
        data[..first].CopyTo(_buffer.AsSpan(tail));
        data[first..].CopyTo(_buffer);
        _count += data.Length;
        HighWaterMark = Math.Max(HighWaterMark, _count);
        return true;
    }

    public byte[] Dequeue(int maximumBytes)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        int take = Math.Min(maximumBytes, _count);
        if (take == 0) return Array.Empty<byte>();
        var output = new byte[take];
        int first = Math.Min(take, Capacity - _head);
        _buffer.AsSpan(_head, first).CopyTo(output);
        _buffer.AsSpan(0, take - first).CopyTo(output.AsSpan(first));
        _head = (_head + take) % Capacity;
        _count -= take;
        return output;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }
}
