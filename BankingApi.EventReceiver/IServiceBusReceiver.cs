using Azure.Messaging.ServiceBus;

namespace BankingApi.EventReceiver;

public interface IServiceBusReceiver
{
    Task<ServiceBusReceivedMessage?> Peek(CancellationToken stoppingToken);
    Task<Task> Abandon(ServiceBusReceivedMessage message, CancellationToken stoppingToken);
    Task<Task> Complete(ServiceBusReceivedMessage? message, CancellationToken stoppingToken);
    Task<Task> ReSchedule(ServiceBusReceivedMessage message, DateTimeOffset nextAvailableTime);
    Task<Task> MoveToDeadLetter(ServiceBusReceivedMessage message);
}