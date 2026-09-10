using System.Linq;
using SecsGemBaseItems.Data_Containers;
using SecsGemBaseItems.Enums;
using SecsGemBaseItems.Responders;
using Xunit;

namespace SecsGemBaseItems.Tests;

/// <summary>
/// Covers the pieces a scenario Receive/Send pair uses for parameterized responses:
/// <see cref="SecsGemItemPath"/> addressing, <see cref="MatchCondition"/> gating, and
/// <see cref="ValueBinding"/> value/branch copying.
/// </summary>
public class ResponderTests
{
    // ---- tree builders -------------------------------------------------------

    private static SecsGemItem Val(SecsGemItemFormatType format, params string[] values)
    {
        var item = SecsGemItem.Create(format);
        item.SetValuesFromStrings(values);
        return item;
    }

    private static SecsGemListItem List(params SecsGemItem[] children)
    {
        var list = new SecsGemListItem();
        foreach (var child in children)
            child.SetParent(list);
        return list;
    }

    private static SecsGemDataMessage Message(byte stream, byte function, params SecsGemItem[] children)
    {
        var message = new SecsGemDataMessage { Stream = stream, Function = function };
        foreach (var child in children)
            child.SetParent(message);
        return message;
    }

    // S6F11: L[ DATAID, CEID, L[ L[ RPTID, L[ CarrierId ] ] ] ]
    private static SecsGemDataMessage BuildS6F11(uint ceid = 500, string carrierId = "123Asb") =>
        Message(6, 11,
            List(
                Val(SecsGemItemFormatType.U4, "0"),
                Val(SecsGemItemFormatType.U4, ceid.ToString()),
                List(
                    List(
                        Val(SecsGemItemFormatType.U4, "1"),
                        List(Val(SecsGemItemFormatType.ASCII, carrierId))))));

    private const string CeidPath = "0.1";
    private const string CarrierIdPath = "0.2.0.1.0";

    // S2F41: L[ RCMD, L[ L[ CPNAME, CPVAL ] ] ]
    private static SecsGemDataMessage BuildS2F41() =>
        Message(2, 41,
            List(
                Val(SecsGemItemFormatType.ASCII),
                List(
                    List(
                        Val(SecsGemItemFormatType.ASCII, "RecipeParam"),
                        Val(SecsGemItemFormatType.ASCII)))));

    private const string RcmdPath = "0.0";
    private const string CpValPath = "0.1.0.1";

    // ---- SecsGemItemPath ---------------------------------------------------

    [Fact]
    public void Path_resolves_nested_items()
    {
        var msg = BuildS6F11();

        Assert.True(SecsGemItemPath.TryResolve(msg, CeidPath, out var ceid));
        Assert.Equal("500", ceid.GetStringValues().Single());

        Assert.True(SecsGemItemPath.TryResolve(msg, CarrierIdPath, out var carrier));
        Assert.Equal("123Asb", carrier.GetStringValues().Single());
    }

    [Fact]
    public void Path_returns_false_for_missing_segment()
    {
        var msg = BuildS6F11();
        Assert.False(SecsGemItemPath.TryResolve(msg, "0.9", out _));
        Assert.False(SecsGemItemPath.TryResolve(msg, "", out _));
    }

    // ---- MatchCondition --------------------------------------------------

    [Theory]
    [InlineData(MatchOperator.Equals, "500", true)]
    [InlineData(MatchOperator.Equals, "501", false)]
    [InlineData(MatchOperator.NotEquals, "501", true)]
    [InlineData(MatchOperator.NotEquals, "500", false)]
    public void Condition_evaluates_against_value(MatchOperator op, string value, bool expected)
    {
        var condition = new MatchCondition { ItemPath = CeidPath, Operator = op, Value = value };
        Assert.Equal(expected, condition.Evaluate(BuildS6F11()));
    }

    [Fact]
    public void Condition_with_missing_path_fails()
    {
        var condition = new MatchCondition { ItemPath = "7.7", Operator = MatchOperator.Equals, Value = "x" };
        Assert.False(condition.Evaluate(BuildS6F11()));
    }

    // ---- ValueBinding --------------------------------------------------

    [Fact]
    public void Binding_literal_sets_value()
    {
        var outgoing = BuildS2F41();
        var binding = new ValueBinding { TargetItemPath = RcmdPath, Source = BindingSourceKind.Literal, SourceRef = "START" };

        Assert.True(binding.Apply(outgoing, BuildS6F11()));
        Assert.True(SecsGemItemPath.TryResolve(outgoing, RcmdPath, out var rcmd));
        Assert.Equal("START", rcmd.GetStringValues().Single());
    }

    [Fact]
    public void Binding_echo_copies_incoming_value()
    {
        var outgoing = BuildS2F41();
        var binding = new ValueBinding { TargetItemPath = CpValPath, Source = BindingSourceKind.Echo, SourceRef = CarrierIdPath };

        Assert.True(binding.Apply(outgoing, BuildS6F11(carrierId: "WAFER-42")));
        Assert.True(SecsGemItemPath.TryResolve(outgoing, CpValPath, out var cpval));
        Assert.Equal("WAFER-42", cpval.GetStringValues().Single());
    }

    [Fact]
    public void Binding_copybranch_replaces_whole_list()
    {
        var outgoing = Message(2, 41, List(Val(SecsGemItemFormatType.ASCII), new SecsGemListItem()));
        var incoming = BuildS6F11();

        var binding = new ValueBinding { TargetItemPath = "0.1", Source = BindingSourceKind.CopyBranch, SourceRef = "0.2" };
        Assert.True(binding.Apply(outgoing, incoming));

        Assert.True(SecsGemItemPath.TryResolve(outgoing, "0.1", out var copied));
        SecsGemItemPath.TryResolve(incoming, "0.2", out var original);
        Assert.True(SecsGemItem.IsEquivalent(copied, original));
    }

    [Fact]
    public void Binding_echo_pulls_from_the_named_source_node()
    {
        var first = BuildS6F11(carrierId: "FROM-FIRST");   // captured by node "rx1"
        var second = BuildS6F11(carrierId: "FROM-SECOND"); // captured by node "rx2"

        SecsGemDataMessage Resolve(string? id) => id == "rx1" ? first : second;

        var outgoing = BuildS2F41();
        var binding = new ValueBinding
        {
            TargetItemPath = CpValPath,
            Source = BindingSourceKind.Echo,
            SourceRef = CarrierIdPath,
            SourceNodeId = "rx1"
        };

        Assert.True(binding.Apply(outgoing, Resolve));
        Assert.True(SecsGemItemPath.TryResolve(outgoing, CpValPath, out var cpval));
        Assert.Equal("FROM-FIRST", cpval.GetStringValues().Single());
    }

    [Fact]
    public void Binding_without_source_node_id_uses_the_resolver_fallback()
    {
        var last = BuildS6F11(carrierId: "LAST");
        var outgoing = BuildS2F41();
        var binding = new ValueBinding { TargetItemPath = CpValPath, Source = BindingSourceKind.Echo, SourceRef = CarrierIdPath };

        Assert.True(binding.Apply(outgoing, _ => last));
        Assert.True(SecsGemItemPath.TryResolve(outgoing, CpValPath, out var cpval));
        Assert.Equal("LAST", cpval.GetStringValues().Single());
    }

    [Fact]
    public void Binding_unresolvable_path_is_a_no_op()
    {
        var outgoing = BuildS2F41();
        var binding = new ValueBinding { TargetItemPath = "9.9", Source = BindingSourceKind.Literal, SourceRef = "X" };
        Assert.False(binding.Apply(outgoing, BuildS6F11()));
    }

    // ---- the scenario Receive → Send flow (what ScenarioExecutionService does) --------

    [Fact]
    public void Receive_conditions_then_send_bindings_produce_parameterized_reply()
    {
        // Receive node: only proceed for an S6F11 whose CEID == 500.
        var conditions = new[] { new MatchCondition { ItemPath = CeidPath, Operator = MatchOperator.Equals, Value = "500" } };
        // Send node: RCMD is a literal, CPVAL echoes the carrier id off the received message.
        var bindings = new[]
        {
            new ValueBinding { TargetItemPath = RcmdPath, Source = BindingSourceKind.Literal, SourceRef = "START" },
            new ValueBinding { TargetItemPath = CpValPath, Source = BindingSourceKind.Echo, SourceRef = CarrierIdPath }
        };

        var received = BuildS6F11(ceid: 500, carrierId: "123Asb");
        Assert.True(conditions.All(c => c.Evaluate(received)));
        Assert.False(conditions.All(c => c.Evaluate(BuildS6F11(ceid: 501))));

        var outgoing = BuildS2F41();
        foreach (var b in bindings)
            Assert.True(b.Apply(outgoing, received));

        Assert.True(SecsGemItemPath.TryResolve(outgoing, RcmdPath, out var rcmd));
        Assert.Equal("START", rcmd.GetStringValues().Single());
        Assert.True(SecsGemItemPath.TryResolve(outgoing, CpValPath, out var cpval));
        Assert.Equal("123Asb", cpval.GetStringValues().Single());
    }

    [Fact]
    public void Conditions_and_bindings_round_trip_through_json()
    {
        var conditions = new[] { new MatchCondition { ItemPath = CeidPath, Operator = MatchOperator.NotEquals, Value = "0" } };
        var bindings = new[] { new ValueBinding { TargetItemPath = CpValPath, Source = BindingSourceKind.CopyBranch, SourceRef = "0.2" } };

        var restoredConditions = ResponderJson.DeserializeConditions(ResponderJson.SerializeConditions(conditions));
        var restoredBindings = ResponderJson.DeserializeBindings(ResponderJson.SerializeBindings(bindings));

        Assert.Equal(MatchOperator.NotEquals, restoredConditions.Single().Operator);
        Assert.Equal(CeidPath, restoredConditions.Single().ItemPath);
        Assert.Equal(BindingSourceKind.CopyBranch, restoredBindings.Single().Source);
        Assert.Equal("0.2", restoredBindings.Single().SourceRef);
    }
}
