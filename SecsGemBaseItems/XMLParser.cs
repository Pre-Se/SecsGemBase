using System.Collections.ObjectModel;
using System.Xml;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Data_Containers.Interfaces;
using SecsGemBaseItems.Enums;

namespace SecsGemBaseItems;
/// <summary>
/// Reads an XML Library containing SecsGemTransactions and their associated SecsGemDataMessages
/// </summary>
public class XmlParser
{
    private XmlDocument Document { get; }

    public XmlParser(string path)
    {
        Document = new XmlDocument();
        Document.Load(path);
    }

    /// <summary>
    /// Reads all transactions contained within the XML file and adds them to the treeItems collection
    /// </summary>
    /// <param name="treeItems"></param>
    public void LoadItems(ObservableCollection<SecsGemTransaction> treeItems)
    {
        var libraryNode = Document.SelectSingleNode("Library");
        if (libraryNode == null) return;

        var nodes = libraryNode.ChildNodes;

        foreach (XmlNode transactionNode in nodes)
        {
            if (!string.Equals(transactionNode.Name, "Transaction")) continue;

            var transactionNameNode = transactionNode.SelectSingleNode("Name");
            var transactionDescriptionNode = transactionNode.SelectSingleNode("Description");
            var streamNode = transactionNode.SelectSingleNode("Stream");
            var functionNode = transactionNode.SelectSingleNode("Function");
            var replyExpectedNode = transactionNode.SelectSingleNode("ReplyExpected");

            if (transactionNameNode == null || transactionDescriptionNode == null ||
                streamNode == null || functionNode == null || replyExpectedNode == null) continue;

            var transactionName = transactionNameNode.InnerText;
            var transactionDescription = transactionDescriptionNode.InnerText;
            SecsGemTransaction transaction = new()
            {
                Name = transactionName,
                Description = transactionDescription
            };

            ReadDataMessages(streamNode, functionNode, replyExpectedNode, transactionNode, transaction);

            treeItems.Add(transaction);
        }
    }

    private static void ReadDataMessages(XmlNode streamNode, XmlNode functionNode, XmlNode replyExpectedNode, XmlNode transaction,
        SecsGemTransaction newTransaction)
    {
        var stream = Convert.ToByte(streamNode.InnerText);
        var function = Convert.ToByte(functionNode.InnerText);
        var replyExpected = string.Equals("true", replyExpectedNode.InnerText);

        CreateDataMessage(transaction, replyExpected, stream, function, newTransaction, true);
        if (replyExpected)
            CreateDataMessage(transaction, false, stream, function, newTransaction, false);
    }

    private static void CreateDataMessage(XmlNode transaction, bool replyExpected, byte stream, byte function,
        SecsGemTransaction newTransaction, bool isPrimary)
    {
        var nodeName = isPrimary ? "Primary" : "Secondary";

        var node = transaction.SelectSingleNode(nodeName);
        if (node == null) return;

        var nameAttribute = node.Attributes?["name"];
        var descAttribute = node.Attributes?["desc"];

        if (nameAttribute == null || descAttribute == null) return;

        SecsGemDataMessage dataMessage = new()
        {
            Name = nameAttribute.Value,
            Description = descAttribute.Value,
            Reply = replyExpected,
            Stream = stream,
            Function = (byte)(isPrimary ? function : function + 1),
            IsPrimary = isPrimary
        };
        switch (isPrimary)
        {
            case true:
                newTransaction.PrimaryMessage = dataMessage;
                break;
            case false:
                newTransaction.ReplyMessage = dataMessage;
                break;
        }
        ReadItems(dataMessage, node.ChildNodes);
    }

    private static void ReadItems(ICanBeParent parent, XmlNodeList nodeList)
    {
        foreach (XmlNode itemNode in nodeList)
        {
            if (!string.Equals(itemNode.Name, "Item")) continue;

            var format = itemNode.SelectSingleNode("Format")?.InnerText;
            var name = itemNode.SelectSingleNode("Name")?.InnerText;
            var description = itemNode.SelectSingleNode("Description")?.InnerText;

            if (format == null || name == null || description == null ||
                !Enum.TryParse(format, out SecsGemItemFormatType dataType)) continue;

            var item = SecsGemItem.Create(dataType);
            item.Name = name;
            item.Description = description;
            item.SetParent(parent);

            var valueNodes = itemNode.SelectNodes("Value");
            if (valueNodes is { Count: > 0 })
            {
                var strings = new string[valueNodes.Count];
                for (var i = 0; i < valueNodes.Count; i++)
                    strings[i] = valueNodes[i]!.InnerText;
                item.SetValuesFromStrings(strings);
            }

            if (dataType == SecsGemItemFormatType.List) ReadItems(item, itemNode.ChildNodes);
        }
    }
}
