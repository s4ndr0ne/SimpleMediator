using SimpleMediator.Interfaces;

namespace SimpleMediator.Infrastructure;

/// <summary>
/// Caches the sorted execution order of the pipeline behaviors of one request/response pair so a
/// dispatch whose behavior types and <see cref="IOrderedPipelineBehavior.Order"/> values are
/// unchanged does not re-allocate and re-sort them every request.
/// </summary>
/// <remarks>
/// The snapshot is validated on every request: both the behavior type sequence and each behavior's
/// <c>Order</c> value are re-read, so scoped or per-instance order values — the contract documented
/// on <see cref="IOrderedPipelineBehavior.Order"/> — still take effect immediately; a change simply
/// recomputes the permutation. Validation reads allocate nothing, and only a mismatch allocates.
/// One instance is owned by each <c>RequestHandlerWrapperImpl</c>, which is itself cached on the
/// container's configuration singleton, so the cache shares the container's lifetime and is never
/// static state visible across containers.
/// </remarks>
internal sealed class BehaviorOrderCache
{
    // One behavior is always the whole pipeline: the sorted permutation is [0] whatever its Order,
    // so the common single-behavior case allocates nothing and never computes anything.
    private static readonly int[] SingleBehaviorOrder = [0];

    // A single reference: concurrent computations publish a whole entry atomically, so a reader
    // never observes types from one computation paired with orders from another.
    private Entry? _entry;

    public int[] GetOrder<TRequest, TResponse>(
        IReadOnlyList<IPipelineBehavior<TRequest, TResponse>> behaviors)
        where TRequest : IRequest<TResponse>
        => GetOrderedIndices(behaviors);

    public int[] GetStreamOrder<TRequest, TResponse>(
        IReadOnlyList<IStreamPipelineBehavior<TRequest, TResponse>> behaviors)
        where TRequest : IStreamRequest<TResponse>
        => GetOrderedIndices(behaviors);

    public int[] GetNotificationOrder<TNotification>(
        IReadOnlyList<INotificationBehavior<TNotification>> behaviors)
        where TNotification : INotification
        => GetOrderedIndices(behaviors);

    private int[] GetOrderedIndices<TBehavior>(IReadOnlyList<TBehavior> behaviors)
        where TBehavior : IOrderedPipelineBehavior
    {
        var count = behaviors.Count;
        if (count <= 1)
        {
            return count == 1 ? SingleBehaviorOrder : Array.Empty<int>();
        }

        var entry = _entry;
        if (entry is not null && Matches(entry, behaviors))
        {
            return entry.Order;
        }

        return ComputeAndStore(behaviors);
    }

    private static bool Matches<TBehavior>(
        Entry entry,
        IReadOnlyList<TBehavior> behaviors)
        where TBehavior : IOrderedPipelineBehavior
    {
        var count = behaviors.Count;
        if (entry.Types.Length != count)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var behavior = behaviors[i];
            if (!ReferenceEquals(entry.Types[i], behavior.GetType()) ||
                entry.Orders[i] != behavior.Order)
            {
                return false;
            }
        }

        return true;
    }

    private int[] ComputeAndStore<TBehavior>(IReadOnlyList<TBehavior> behaviors)
        where TBehavior : IOrderedPipelineBehavior
    {
        var count = behaviors.Count;

        // Snapshot the identity and order values exactly once, as the pipeline has always done:
        // reading Order inside the comparison would call the property O(n log n) times and, if the
        // value is not stable, could make Array.Sort see an inconsistent ordering.
        var types = new Type[count];
        var orders = new int[count];
        for (var i = 0; i < count; i++)
        {
            var behavior = behaviors[i];
            types[i] = behavior.GetType();
            orders[i] = behavior.Order;
        }

        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            var cmp = orders[b].CompareTo(orders[a]);
            // v4 contract: for equal Order the first registered behavior becomes the outermost one
            // and runs first (FIFO, like MediatR / ASP.NET Core middleware). The wrapping loop in
            // RequestHandlerPipeline consumes this list left to right, so reverse the tie order
            // here to achieve FIFO execution.
            return cmp != 0 ? cmp : b.CompareTo(a);
        });

        _entry = new Entry(types, orders, order);
        return order;
    }

    private sealed class Entry(Type[] types, int[] orders, int[] order)
    {
        public Type[] Types { get; } = types;

        public int[] Orders { get; } = orders;

        public int[] Order { get; } = order;
    }
}
