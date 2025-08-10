using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BankingApi.EventReceiver;

public class MessageWorker
    : IServiceBusReceiver
{
    private readonly ServiceBusReceiver _serviceBusReceiver;
    private readonly ServiceBusSender _serviceBusSender;
    private readonly BankingApiDbContext _bankingApiDbContext;
    private readonly ILogger<MessageWorker> _logger;

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public MessageWorker(IConfiguration config, BankingApiDbContext bankingApiDbContext, ILogger<MessageWorker> logger)
    {
        var serviceBusClient = new ServiceBusClient(config["MessagingConfig:ConnectionString"]);

        var queueName = config["MessagingConfig:QueueName"]!;

        _serviceBusReceiver = serviceBusClient.CreateReceiver(queueName);
        _serviceBusSender = serviceBusClient.CreateSender(queueName);
        _bankingApiDbContext = bankingApiDbContext;
        _logger = logger;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    public async Task<Task> Start(CancellationToken stoppingToken)
    {
        ServiceBusReceivedMessage? message = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                message = await Peek(stoppingToken);

                if (message is null)
                {
                    _logger.LogError("No messages available to process.");
                    return Task.FromResult(Task.FromCanceled(stoppingToken));
                }

                _logger.LogInformation("Message with Id -> {MessageId} was received ", message.MessageId);
                var deserializedMessage = await DeserializeMessage(message);

                await CheckIfDeadLetter(deserializedMessage, message);

                var receivedMessage = message;
                await ((Task)PerformOperations(deserializedMessage, stoppingToken)
                        .ContinueWith(
                            task => task.IsCompletedSuccessfully
                                ? Complete(receivedMessage, stoppingToken)
                                : Abandon(receivedMessage, stoppingToken), stoppingToken))
                    .WaitAsync(stoppingToken);

                await Complete(message, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while processing the message. Message will be processed again in 10m");
            await ReSchedule(message!, new DateTimeOffset(DateTime.UtcNow.AddMinutes(10)));
        }

        return Task.FromResult(Task.CompletedTask);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    public async Task<ServiceBusReceivedMessage?> Peek(CancellationToken stoppingToken) =>
        await _serviceBusReceiver.PeekMessageAsync(cancellationToken: stoppingToken);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    public async Task<Task> Abandon(ServiceBusReceivedMessage? message, CancellationToken stoppingToken)
    {
        await _serviceBusReceiver.AbandonMessageAsync(message, cancellationToken: stoppingToken);

        _logger.LogInformation("Message with Id -> {MessageId} was abandoned ", message!.MessageId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="serviceBusReceivedMessage"></param>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    public async Task<Task> Complete(ServiceBusReceivedMessage? serviceBusReceivedMessage, CancellationToken stoppingToken)
    {
        await _serviceBusReceiver.CompleteMessageAsync(serviceBusReceivedMessage, stoppingToken);

        _logger.LogInformation("Message with Id -> {MessageId} was completed ", serviceBusReceivedMessage!.MessageId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    /// <param name="nextAvailableTime"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async Task<Task> ReSchedule(ServiceBusReceivedMessage message, DateTimeOffset nextAvailableTime)
    {
        var sequenceNumber = await _serviceBusSender.ScheduleMessageAsync(new ServiceBusMessage(message), nextAvailableTime);

        await _serviceBusSender.CancelScheduledMessageAsync(sequenceNumber);

        _logger.LogInformation("Message with Id -> {MessageId} was rescheduled ", message.MessageId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public async Task<Task> MoveToDeadLetter(ServiceBusReceivedMessage? message)
    {
        await _serviceBusReceiver.DeadLetterMessageAsync(message, "sample reason", "sample description");

        _logger.LogInformation("Message with Id -> {MessageId} was moved to dead letter queue ", message!.MessageId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static Task<EventMessage> DeserializeMessage(ServiceBusReceivedMessage? message) =>
        Task.FromResult(JsonSerializer.Deserialize<EventMessage>(message!.Body.ToString(), s_readOptions)
                        ?? throw new InvalidOperationException("Failed to deserialize message body."));

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    private async Task<Task> PerformOperations(EventMessage? message, CancellationToken stoppingToken)
    {
        BankAccount? bankAccount = await _bankingApiDbContext.BankAccounts
            .Where(x => x.Id == message!.BankAccountId)
            .FirstOrDefaultAsync(stoppingToken);

        if (bankAccount is null)
        {
            const string nobankAccountMessage = "No bank account was found.";

            _logger.LogError(nobankAccountMessage);
            return Task.FromResult(Task.FromException(new Exception(nobankAccountMessage)));
        }

        switch (message!.MessageType)
        {
            case MessageType.Debit:
                bankAccount.Balance -= message.Amount;
                break;
            case MessageType.Credit:
                bankAccount.Balance += message.Amount;
                break;
            case MessageType.Info:
            case MessageType.Error:
                break;
            default:
                const string noMessageTypeMessage = "Message Type does not exist.";

                _logger.LogError(noMessageTypeMessage);
                return Task.FromResult(Task.FromException(new Exception(noMessageTypeMessage)));
        }

        _bankingApiDbContext.BankAccounts.Update(bankAccount);
        await _bankingApiDbContext.SaveChangesAsync(stoppingToken);

        _logger.LogInformation("Bank account with Id -> {BankAccountId} was updated with new balance -> {Balance}",
            bankAccount.Id, bankAccount.Balance);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    /// <param name="serviceBusReceivedMessage"></param>
    /// <returns></returns>
    private async Task CheckIfDeadLetter(EventMessage message, ServiceBusReceivedMessage? serviceBusReceivedMessage)
    {
        if (message.MessageType.In(MessageType.Info, MessageType.Error) || serviceBusReceivedMessage!.DeliveryCount > 3)
        {
            await MoveToDeadLetter(serviceBusReceivedMessage);
        }
    }
}