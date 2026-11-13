using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace IAM.BuildingBlocks.Security;

/// <summary>Endpoint metadata: the most body bytes this endpoint accepts.</summary>
public sealed record RequestBodyLimitMetadata(long MaxBytes);

/// <summary>
/// Per-endpoint request body limits (BACKEND_INPUT_VALIDATION step 2), enforced while the body is read, before any
/// binding: a declared length over the limit is refused at once (413), and a body without a declared length (chunked)
/// is cut off as soon as it passes the limit. Endpoint filters can't do this: they run after binding has read the body.
/// </summary>
public static class RequestBodyLimits
{
    public static TBuilder RequireBodyLimit<TBuilder>(this TBuilder builder, long maxBytes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return builder.WithMetadata(new RequestBodyLimitMetadata(maxBytes));
    }

    /// <summary>Must run after routing (so the endpoint is known) and before the endpoint reads its body.</summary>
    public static IApplicationBuilder UseIamRequestBodyLimits(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static (context, next) =>
        {
            var limit = context.GetEndpoint()?.Metadata.GetMetadata<RequestBodyLimitMetadata>();
            if (limit is null)
            {
                return next(context);
            }

            if (context.Request.ContentLength > limit.MaxBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return Task.CompletedTask;
            }

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } server)
            {
                server.MaxRequestBodySize = limit.MaxBytes;
            }

            // Also bound the stream itself, whatever the server supports (for example the test server).
            context.Request.Body = new LimitedReadStream(context.Request.Body, limit.MaxBytes);
            return next(context);
        });
    }

    /// <summary>Reads through to the inner stream and throws a 413 once more than the limit has been read.</summary>
    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > maxBytes
                ? throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge)
                : read;
        }
    }
}
