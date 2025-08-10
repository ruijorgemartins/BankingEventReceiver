namespace BankingApi.EventReceiver;

public abstract record EventMessage(Guid Id, MessageType MessageType, Guid BankAccountId, decimal Amount);