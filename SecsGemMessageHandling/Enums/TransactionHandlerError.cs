namespace SecsGemMessageHandling.Enums;
public enum TransactionHandlerError
{
    None = 0,
    MessageNotSent = 1,
    IncorrectMessageReplyType = 2,
    DoesNotRequireAReply = 3,
    Timeout = 4,
    PassedIncorrectSessionType = 5,
    CantSendMessage = 6,
    Cancelled = 7
}
