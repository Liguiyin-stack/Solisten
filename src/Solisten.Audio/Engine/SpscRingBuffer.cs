namespace Solisten.Audio.Engine;

/// <summary>
/// 单生产者单消费者无锁环形缓冲区（float 采样）。读写索引单调递增（long），
/// 生产者只写 _write，消费者只写 _read；两边都不加锁、不分配内存。
/// </summary>
public sealed class SpscRingBuffer
{
    private readonly float[] _data;
    private readonly int _mask;
    private long _write;
    private long _read;

    public SpscRingBuffer(int minCapacity)
    {
        int cap = 1;
        while (cap < minCapacity) cap <<= 1;
        _data = new float[cap];
        _mask = cap - 1;
    }

    public int Capacity => _data.Length;

    /// <summary>累计写入的采样数（生产者视角，即"全局采样序号"）。</summary>
    public long TotalWritten => Volatile.Read(ref _write);

    /// <summary>累计读出的采样数。</summary>
    public long TotalRead => Volatile.Read(ref _read);

    public int Available => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));

    public int Free => Capacity - Available;

    /// <summary>生产者：写入尽可能多的采样，返回写入数量。</summary>
    public int Write(ReadOnlySpan<float> src)
    {
        long w = _write;
        long r = Volatile.Read(ref _read);
        int n = Math.Min(src.Length, Capacity - (int)(w - r));
        if (n <= 0) return 0;
        int start = (int)(w & _mask);
        int first = Math.Min(n, Capacity - start);
        src[..first].CopyTo(_data.AsSpan(start, first));
        if (n > first) src.Slice(first, n - first).CopyTo(_data.AsSpan(0, n - first));
        Volatile.Write(ref _write, w + n);
        return n;
    }

    /// <summary>消费者：读出最多 dst.Length 个采样。</summary>
    public int Read(Span<float> dst)
    {
        long r = _read;
        long w = Volatile.Read(ref _write);
        int n = Math.Min(dst.Length, (int)(w - r));
        if (n <= 0) return 0;
        int start = (int)(r & _mask);
        int first = Math.Min(n, Capacity - start);
        _data.AsSpan(start, first).CopyTo(dst);
        if (n > first) _data.AsSpan(0, n - first).CopyTo(dst[first..]);
        Volatile.Write(ref _read, r + n);
        return n;
    }

    /// <summary>消费者：丢弃当前所有可读数据（flush）。</summary>
    public void DiscardAll() => Volatile.Write(ref _read, Volatile.Read(ref _write));
}

/// <summary>单生产者单消费者无锁定长队列（值类型元素，预分配）。</summary>
public sealed class SpscQueue<T> where T : struct
{
    private readonly T[] _items;
    private readonly int _mask;
    private long _head;
    private long _tail;

    public SpscQueue(int minCapacity)
    {
        int cap = 1;
        while (cap < minCapacity) cap <<= 1;
        _items = new T[cap];
        _mask = cap - 1;
    }

    public bool TryEnqueue(in T item)
    {
        long t = _tail;
        if (t - Volatile.Read(ref _head) >= _items.Length) return false;
        _items[t & _mask] = item;
        Volatile.Write(ref _tail, t + 1);
        return true;
    }

    public bool TryPeek(out T item)
    {
        long h = _head;
        if (h >= Volatile.Read(ref _tail)) { item = default; return false; }
        item = _items[h & _mask];
        return true;
    }

    public bool TryDequeue(out T item)
    {
        long h = _head;
        if (h >= Volatile.Read(ref _tail)) { item = default; return false; }
        item = _items[h & _mask];
        Volatile.Write(ref _head, h + 1);
        return true;
    }

    /// <summary>消费者：清空队列。</summary>
    public void Clear() => Volatile.Write(ref _head, Volatile.Read(ref _tail));
}
