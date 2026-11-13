using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SimpleMediator.Interfaces;

namespace SimpleMediator.Outbox;

/// <summary>Configures AOT-safe notification type mappings for <see cref="JsonOutboxSerializer"/>.</summary>
public sealed class JsonOutboxSerializerBuilder
{
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, Entry> _byType = [];

    /// <summary>Registers a notification type with a stable type key and <see cref="JsonTypeInfo{T}"/>.</summary>
    /// <typeparam name="TNotification">The notification type.</typeparam>
    /// <param name="typeKey">Stable application-defined type key stored in <see cref="OutboxMessage.Type"/>.</param>
    /// <param name="jsonTypeInfo">Source-generated or configured JSON type metadata for <typeparamref name="TNotification"/>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    public JsonOutboxSerializerBuilder AddNotification<TNotification>(
        string typeKey,
        JsonTypeInfo<TNotification> jsonTypeInfo)
        where TNotification : INotification
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var notificationType = typeof(TNotification);
        if (_byKey.ContainsKey(typeKey))
        {
            throw new ArgumentException($"An outbox notification with type key '{typeKey}' is already registered.", nameof(typeKey));
        }

        if (_byType.ContainsKey(notificationType))
        {
            throw new ArgumentException($"Outbox notification type '{notificationType.FullName}' is already registered.", nameof(typeKey));
        }

        var entry = new Entry(
            typeKey,
            notification => JsonSerializer.Serialize((TNotification)notification, jsonTypeInfo),
            payload =>
            {
                var deserialized = JsonSerializer.Deserialize(payload, jsonTypeInfo)
                    ?? throw new InvalidOperationException($"Deserializing outbox message '{typeKey}' produced null.");
                return new OutboxDispatchable<TNotification>(deserialized);
            });

        _byKey.Add(typeKey, entry);
        _byType.Add(notificationType, entry);
        return this;
    }

    /// <summary>Builds an immutable <see cref="JsonOutboxSerializer"/> from the registered mappings.</summary>
    public JsonOutboxSerializer Build()
        => new(_byKey.ToFrozenDictionary(StringComparer.Ordinal), _byType.ToFrozenDictionary());

    internal sealed record Entry(
        string TypeKey,
        Func<INotification, string> Serialize,
        Func<string, IOutboxDispatchable> Deserialize);
}
