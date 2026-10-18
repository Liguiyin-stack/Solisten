using System.Collections.Immutable;
using Solisten.Core.Model;

namespace Solisten.Core;

public interface IEffectHandler
{
    void Handle(Effect effect, PlayerStore store);
}

public sealed class StateChangedEventArgs(AppState previous, AppState current, AppAction action) : EventArgs
{
    public AppState Previous { get; } = previous;
    public AppState Current { get; } = current;
    public AppAction Action { get; } = action;
}

/// <summary>
/// 持有不可变 AppState。Dispatch 可在任意线程调用；动作按到达顺序串行归约，
/// 副作用在归约之后依次交给 <see cref="IEffectHandler"/>，副作用中再次 Dispatch 会排队而不是重入。
/// </summary>
public sealed class PlayerStore
{
    private readonly object _gate = new();
    private readonly Queue<AppAction> _pending = new();
    private readonly List<IEffectHandler> _handlers = [];
    private bool _draining;
    private AppState _state;

    public PlayerStore(AppState? initial = null) => _state = initial ?? AppState.Initial();

    public AppState State => Volatile.Read(ref _state);

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public void AddEffectHandler(IEffectHandler handler)
    {
        lock (_gate) _handlers.Add(handler);
    }

    public void Dispatch(AppAction action)
    {
        lock (_gate)
        {
            _pending.Enqueue(action);
            if (_draining) return;
            _draining = true;
        }

        while (true)
        {
            AppAction next;
            IEffectHandler[] handlers;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out next!))
                {
                    _draining = false;
                    return;
                }
                handlers = [.. _handlers];
            }

            var previous = _state;
            ImmutableArray<Effect> effects;
            try
            {
                var result = Reducer.Reduce(previous, next);
                Volatile.Write(ref _state, result.State);
                effects = result.Effects;
            }
            catch
            {
                lock (_gate) _draining = false;
                throw;
            }

            if (!ReferenceEquals(previous, _state)) StateChanged?.Invoke(this, new StateChangedEventArgs(previous, _state, next));
            foreach (var effect in effects)
            {
                foreach (var h in handlers) h.Handle(effect, this);
            }
        }
    }
}
