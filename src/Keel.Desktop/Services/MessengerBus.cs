using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Messaging;

namespace Keel.Desktop.Services;

/// <summary><see cref="IMessageBus"/> over CommunityToolkit's <see cref="WeakReferenceMessenger"/>.</summary>
public sealed class MessengerBus(IMessenger messenger) : IMessageBus
{
    /// <inheritdoc />
    public void Publish<TMessage>(TMessage message)
        where TMessage : class => messenger.Send(message);
}
