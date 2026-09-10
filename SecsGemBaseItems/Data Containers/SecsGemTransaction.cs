using SecsGemBaseItems.Data_Containers.Interfaces;

namespace SecsGemBaseItems.Data_Containers;

/// <summary>
/// A Primary <see cref="SecsGemDataMessage"/> and its associated Reply, if required. Also, an HSMS Control
/// Message of the request(.req) type, and its response Control Message(.rsp), if required
/// </summary>
public partial class SecsGemTransaction : DataItem, ICanBeParent, IDeepCloneable<SecsGemTransaction>
{
    /// <summary>
    /// An HSMS <see cref="SecsGemDataMessage"/> with an odd numbered Function. Also, the first message of a
    /// data transaction.
    /// </summary>
    public SecsGemDataMessage PrimaryMessage
    {
        get => GetMessage(0);
        set
        {
            Children[0] = value;
            value.SetParent(this);
        }
    }

    /// <summary>
    /// An HSMS <see cref="SecsGemDataMessage"/> with an even-numbered function. Also, the appropriate response to <see cref="PrimaryMessage"/>
    /// </summary>
    public SecsGemDataMessage ReplyMessage
    {
        get => GetMessage(1);
        set
        {
            Children[1] = value;
            value.SetParent(this);
        }
    }

    public SecsGemTransaction()
    {
        Name = "New Transaction";
        Children.Add(new SecsGemDataMessage());
        Children.Add(new SecsGemDataMessage());
        PrimaryMessage = new SecsGemDataMessage();
        ReplyMessage = new SecsGemDataMessage();
    }

    /// <summary>
    /// Checks if the <see cref="receivedDataMessage"/> stream and function match the ones from the <see cref="ReplyMessage"/>
    /// </summary>
    /// <returns>true when the expected <see cref="ReplyMessage"/> matches the <see cref="receivedDataMessage"/></returns>
    public bool CheckReceivedReply(SecsGemDataMessage receivedDataMessage)
    {
        return ReplyMessage.Stream == receivedDataMessage.Stream
               && ReplyMessage.Function == receivedDataMessage.Function;
    }

    private SecsGemDataMessage GetMessage(int index)
    {
        if (Children[index] is SecsGemDataMessage message)
        {
            return message;
        }
        var defaultMessage = new SecsGemDataMessage();
        Children[index] = defaultMessage;
        return defaultMessage;
    }

    public SecsGemTransaction Clone()
    {
        var clone = new SecsGemTransaction
        {
            Name = Name,
            Description = Description,
            PrimaryMessage = (SecsGemDataMessage)PrimaryMessage.Clone(),
            ReplyMessage = (SecsGemDataMessage)ReplyMessage.Clone()
        };
        return clone;
    }

    public bool CanAddChild(IDataItem? child) => false;
    public bool TryAddChild(IDataItem child) => false;
    public bool TryRemoveChild(IDataItem child) => false;
}