using Sentry.Extensibility;
using Sentry.Infrastructure;

namespace Sentry.Internal;

internal sealed class DefaultSentryStructuredLogger : SentryStructuredLogger, IDisposable
{
    private readonly IHub _hub;
    private readonly SentryOptions _options;
    private readonly ISystemClock _clock;

    private readonly BatchProcessor<SentryLog> _batchProcessor;

    internal DefaultSentryStructuredLogger(IHub hub, SentryOptions options, ISystemClock clock, int batchCount, TimeSpan batchInterval)
    {
        Debug.Assert(hub.IsEnabled);

        _hub = hub;
        _options = options;
        _clock = clock;

        _batchProcessor = new SentryLogBatchProcessor(hub, batchCount, batchInterval, _options.ClientReportRecorder, _options.DiagnosticLogger);
    }

    /// <inheritdoc />
    private protected override void CaptureLog(SentryLogLevel level, string template, object[]? parameters, Action<SentryLog>? configureLog)
    {
        var timestamp = _clock.GetUtcNow();
        _hub.GetTraceIdAndSpanId(out var traceId, out var spanId);

        string message;
        ImmutableArray<KeyValuePair<string, object>> @params;

        if (parameters is { Length: > 0 })
        {
            try
            {
                message = string.Format(CultureInfo.InvariantCulture, template, parameters);
            }
            catch (FormatException e)
            {
                _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.Invalid, DataCategory.LogItem);
                _options.DiagnosticLogger?.LogError(e, "Template string does not match the provided argument. The Log will be dropped.");
                return;
            }

            var builder = ImmutableArray.CreateBuilder<KeyValuePair<string, object>>(parameters.Length);
            for (var index = 0; index < parameters.Length; index++)
            {
                builder.Add(new KeyValuePair<string, object>(index.ToString(), parameters[index]));
            }
            @params = builder.DrainToImmutable();
        }
        else
        {
            message = template;
            template = null!; // SentryLog.Template is declared nullable (string?)
            @params = ImmutableArray<KeyValuePair<string, object>>.Empty;
        }

        SentryLog log = new(timestamp, traceId, level, message)
        {
            Template = template,
            Parameters = @params,
            SpanId = spanId,
        };

        try
        {
            configureLog?.Invoke(log);
        }
        catch (Exception e)
        {
            _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.CallbackError, DataCategory.LogItem);
            _options.DiagnosticLogger?.LogError(e, "The configureLog callback threw an exception. The Log will be dropped.");
            return;
        }

        var scope = _hub.GetScope();
        log.SetDefaultAttributes(_options, scope);

        if (TryCaptureLog(log, out var capturedLog))
        {
            PrintIfDebug(capturedLog);
        }
    }

    /// <inheritdoc />
    protected internal override void CaptureLog(SentryLog log)
    {
        _ = TryCaptureLog(log, out _);
    }

    private bool TryCaptureLog(SentryLog log, [NotNullWhen(true)] out SentryLog? capturedLog)
    {
        capturedLog = null;
        var configuredLog = log;

        if (_options.BeforeSendLogInternal is { } beforeSendLog)
        {
            try
            {
                configuredLog = beforeSendLog.Invoke(log);
            }
            catch (Exception e)
            {
                _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.CallbackError, DataCategory.LogItem);
                _options.DiagnosticLogger?.LogError(e, "The BeforeSendLog callback threw an exception. The Log will be dropped.");
                return false;
            }
        }

        if (configuredLog is null)
        {
            _options.ClientReportRecorder.RecordDiscardedEvent(DiscardReason.BeforeSend, DataCategory.LogItem);
            _options.DiagnosticLogger?.LogInfo("Log dropped by BeforeSendLog callback.");
            return false;
        }

        _batchProcessor.Enqueue(configuredLog);
        capturedLog = configuredLog;
        return true;
    }

    // When Debug is enabled, calls to the logger API should also print the log.
    // See https://develop.sentry.dev/sdk/telemetry/logs/
    private void PrintIfDebug(SentryLog log)
    {
        if (_options.DiagnosticLogger is not { } logger)
        {
            return;
        }

        var level = log.Level.ToSentryLevel();
        if (logger.IsEnabled(level))
        {
            var (severityText, _) = log.Level.ToSeverityTextAndOptionalSeverityNumber(null);
            logger.Log(level, "Log [{0}]: {1}", null, severityText, log.Message);
        }
    }

    /// <inheritdoc />
    protected internal override void Flush()
    {
        _batchProcessor.Flush();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _batchProcessor.Dispose();
    }
}
