namespace SimpleMediator.Interfaces;

/// <summary>
/// Defines a mediator to encapsulate request/response and publish/subscribe interactions.
/// </summary>
/// <remarks>
/// <see cref="IMediator"/> combines <see cref="ISender"/> and <see cref="IPublisher"/>. Prefer the
/// narrower interface when a component only sends requests or only publishes notifications. All
/// three are registered by <c>AddSimpleMediator</c> and resolve to the same implementation.
/// </remarks>
public interface IMediator : ISender, IPublisher
{
}
