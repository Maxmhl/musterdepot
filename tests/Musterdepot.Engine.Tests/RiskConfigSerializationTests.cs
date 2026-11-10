using Musterdepot.Core.Domain;
using Musterdepot.Core.Events;
using Musterdepot.Core.Serialization;

namespace Musterdepot.Engine.Tests;

public class RiskConfigSerializationTests
{
    [Fact]
    public void UnlimitedRiskLimits_SurviveJsonRoundTrip()
    {
        var risk = RiskConfig.Default with { MaxDailyLoss = null, MaxOrderNotional = null, MaxPositionNotional = null };
        var e = new RiskConfigChanged { AccountId = "main", TimestampUtc = DateTimeOffset.UnixEpoch, Risk = risk };
        var back = (RiskConfigChanged)JsonDefaults.DeserializeEvent(JsonDefaults.SerializeEvent(e));
        Assert.Null(back.Risk.MaxDailyLoss);
        Assert.Null(back.Risk.MaxOrderNotional);
        Assert.Null(back.Risk.MaxPositionNotional);
        Assert.Equal(risk.MaxOrderPercentOfEquity, back.Risk.MaxOrderPercentOfEquity);
    }
}
