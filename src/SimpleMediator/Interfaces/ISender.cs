using System.Diagnostics.CodeAnalysis;

namespace SimpleMediator.Interfaces;

/// <summary>
/// Sends requests to their single registered handler (the request/response half of the mediator).
/// </summary>
/// <remarks>
/// Depend on <see cref="ISender"/> instead of <see cref="IMediator"/> when a component only needs to
/// dispatch commands or queries. Resolve it inside the request or operation scope, exactly like
/// <see cref="IMediator"/>.
/// </remarks>
public interface ISender
{
    /// <summary>
    /// Asynchronously sends a request to its single registered handler and returns the response.
    /// </summary>
    /// <typeparam name="TResponse">The type of the expected response.</typeparam>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation that yields the response.</returns>
    [RequiresUnreferencedCode("SimpleMediator creates request wrappers and resolves handlers through reflection.")]
    [RequiresDynamicCode("SimpleMediator constructs generic request wrapper types at runtime.")]
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously sends a void request (<see cref="IRequest"/>) to its single registered handler.
    /// </summary>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RequiresUnreferencedCode("SimpleMediator creates request wrappers and resolves handlers through reflection.")]
    [RequiresDynamicCode("SimpleMediator constructs generic request wrapper types at runtime.")]
    Task Send(IRequest request, CancellationToken cancellationToken = default);
}
