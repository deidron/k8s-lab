using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;

namespace K8sLab.Tests;

/// <summary>
/// Checks nlog.config itself, because nothing else does. It is XML the
/// compiler never sees, and every field in it feeds something downstream:
/// the JSON shape is what Loki parses, trace_id is what links a log line to
/// its trace, and the Microsoft.* rule is what keeps framework chatter out.
/// A dependency bump can break any of it while the application keeps
/// serving traffic perfectly.
/// </summary>
public sealed class LoggingConfigurationTests : IClassFixture<AppFactory>
{
    private readonly LoggingConfiguration _config;

    public LoggingConfigurationTests(AppFactory factory)
    {
        // Booting the app is what loads nlog.config. With
        // throwConfigExceptions="true" a broken config fails here.
        factory.CreateClient();

        _config = LogManager.Configuration
                  ?? throw new InvalidOperationException("NLog configuration was not loaded");
    }

    [Fact]
    public void Console_target_exists()
    {
        Target? target = _config.FindTargetByName("jsonConsole");

        Assert.NotNull(target);
        Assert.IsType<ConsoleTarget>(target);
    }

    [Fact]
    public void Loki_target_exists()
    {
        // Also proves the NLog.Loki extension resolved. The assembly name
        // differs from the package name, and getting it wrong makes NLog
        // discard the whole configuration without a word.
        Target? target = _config.FindTargetByName("loki");

        Assert.NotNull(target);
    }

    [Theory]
    [InlineData("time_stamp")]
    [InlineData("log_level")]
    [InlineData("log_message")]
    [InlineData("logger_name")]
    [InlineData("trace_id")]
    [InlineData("exception_details")]
    public void Json_layout_keeps_the_field(string attributeName)
    {
        // The shared layout lives in a variable so console and Loki cannot
        // drift apart; that is where the JSON shape is actually defined.
        Layout? shared = _config.Variables["jsonLayout"];
        JsonLayout layout = Assert.IsType<JsonLayout>(shared);

        Assert.Contains(layout.Attributes, a => a.Name == attributeName);
    }

    [Fact]
    public void Framework_chatter_is_silenced()
    {
        // A rule that matches Microsoft.* up to Info, writes nowhere and
        // stops processing. Without it every request adds three or four
        // lines of framework noise.
        LoggingRule? rule = _config.LoggingRules
            .FirstOrDefault(r => r.LoggerNamePattern == "Microsoft.*");

        Assert.NotNull(rule);
        Assert.True(rule.Final);
        Assert.Empty(rule.Targets);

        // IsLoggingEnabledForLevel means "this rule applies to that level",
        // not "a line gets written". Info matching is the point: the rule
        // captures it, writes nowhere and stops. Warn must not match, so it
        // falls through to the rule below and still reaches the targets.
        Assert.True(rule.IsLoggingEnabledForLevel(LogLevel.Info));
        Assert.False(rule.IsLoggingEnabledForLevel(LogLevel.Warn));
    }
}
