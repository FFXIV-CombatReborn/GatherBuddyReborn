using Serilog.Events;
using System;
using System.Runtime.CompilerServices;

namespace GatherBuddy;

public sealed class Logger
{
    private readonly Serilog.ILogger _log;
    private readonly string _prefix;

    public Logger(string pluginName)
    {
        _log = Serilog.Log.ForContext("Dalamud.PluginName", pluginName);
        _prefix = $"[{pluginName}] ";
    }

    public bool IsEnabled(LogEventLevel level)
        => _log.IsEnabled(level);

    public void Fatal(string text)
        => Write(LogEventLevel.Fatal, text);

    public void Fatal([InterpolatedStringHandlerArgument("")] Message<FatalLevel> message)
        => Write(LogEventLevel.Fatal, ref message);

    public void Error(string text)
        => Write(LogEventLevel.Error, text);

    public void Error([InterpolatedStringHandlerArgument("")] Message<ErrorLevel> message)
        => Write(LogEventLevel.Error, ref message);

    public void Warning(string text)
        => Write(LogEventLevel.Warning, text);

    public void Warning([InterpolatedStringHandlerArgument("")] Message<WarningLevel> message)
        => Write(LogEventLevel.Warning, ref message);

    public void Information(string text)
        => Write(LogEventLevel.Information, text);

    public void Information([InterpolatedStringHandlerArgument("")] Message<InformationLevel> message)
        => Write(LogEventLevel.Information, ref message);

    public void Debug(string text)
        => Write(LogEventLevel.Debug, text);

    public void Debug([InterpolatedStringHandlerArgument("")] Message<DebugLevel> message)
        => Write(LogEventLevel.Debug, ref message);

    public void Verbose(string text)
        => Write(LogEventLevel.Verbose, text);

    public void Verbose(string template, params object?[] values)
        => _log.Verbose(_prefix + template, values);

    public void Verbose([InterpolatedStringHandlerArgument("")] Message<VerboseLevel> message)
        => Write(LogEventLevel.Verbose, ref message);

    private void Write(LogEventLevel level, string text)
    {
        if (_log.IsEnabled(level))
        {
            _log.Write(level, _prefix + text);
        }
    }

    private void Write<TLevel>(LogEventLevel level, ref Message<TLevel> message) where TLevel : ILogLevel
    {
        if (message.Enabled)
        {
            _log.Write(level, message.ToStringAndClear());
        }
    }

    public interface ILogLevel
    {
        static abstract LogEventLevel Level { get; }
    }

    public readonly struct FatalLevel : ILogLevel
    {
        public static LogEventLevel Level
            => LogEventLevel.Fatal;
    }

    public readonly struct ErrorLevel : ILogLevel
    {
        public static LogEventLevel Level
            => LogEventLevel.Error;
    }

    public readonly struct WarningLevel : ILogLevel
    {
        public static LogEventLevel Level
            => LogEventLevel.Warning;
    }

    public readonly struct InformationLevel : ILogLevel
    {
        public static LogEventLevel Level
            => LogEventLevel.Information;
    }

    public readonly struct DebugLevel : ILogLevel
    {
        public static LogEventLevel Level
            => LogEventLevel.Debug;
    }

    public readonly struct VerboseLevel : ILogLevel
    {
        public static LogEventLevel Level
            => LogEventLevel.Verbose;
    }

    [InterpolatedStringHandler]
    public ref struct Message<TLevel> where TLevel : ILogLevel
    {
        private DefaultInterpolatedStringHandler _builder;

        internal readonly bool Enabled;

        public Message(int literalLength, int formattedCount, Logger logger, out bool isEnabled)
        {
            isEnabled = Enabled = logger._log.IsEnabled(TLevel.Level);
            if (!isEnabled)
            {
                _builder = default;
                return;
            }

            _builder = new DefaultInterpolatedStringHandler(literalLength + logger._prefix.Length, formattedCount);
            _builder.AppendLiteral(logger._prefix);
        }

        public void AppendLiteral(string value)
            => _builder.AppendLiteral(value);

        public void AppendFormatted<T>(T value)
            => _builder.AppendFormatted(value);

        public void AppendFormatted<T>(T value, string? format)
            => _builder.AppendFormatted(value, format);

        public void AppendFormatted<T>(T value, int alignment)
            => _builder.AppendFormatted(value, alignment);

        public void AppendFormatted<T>(T value, int alignment, string? format)
            => _builder.AppendFormatted(value, alignment, format);

        public void AppendFormatted(scoped ReadOnlySpan<char> value)
            => _builder.AppendFormatted(value);

        public void AppendFormatted(string? value)
            => _builder.AppendFormatted(value);

        internal string ToStringAndClear()
            => _builder.ToStringAndClear();
    }
}
