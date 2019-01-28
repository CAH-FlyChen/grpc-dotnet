#region Copyright notice and license

// Copyright 2019 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Grpc.AspNetCore.Server.Internal
{
    internal class ReadOnlySequenceStream : Stream
    {
        private static readonly Task<int> ZeroTask = Task.FromResult(0);

        private Task<int> _lastReadTask;
        private ReadOnlySequence<byte> _readOnlySequence;
        private SequencePosition _position;

        public ReadOnlySequenceStream(ReadOnlySequence<byte> readOnlySequence)
        {
            _readOnlySequence = readOnlySequence;
            _position = readOnlySequence.Start;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _readOnlySequence.Slice(0, _position).Length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadOnlySequence<byte> remaining = _readOnlySequence.Slice(_position);
            ReadOnlySequence<byte> toCopy = remaining.Slice(0, Math.Min(count, remaining.Length));
            this._position = toCopy.End;
            toCopy.CopyTo(buffer.AsSpan(offset, count));
            return (int)toCopy.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead = Read(buffer, offset, count);
            if (bytesRead == 0)
            {
                return ZeroTask;
            }

            if (_lastReadTask?.Result == bytesRead)
            {
                return _lastReadTask;
            }
            else
            {
                return _lastReadTask = Task.FromResult(bytesRead);
            }
        }

        public override int ReadByte()
        {
            ReadOnlySequence<byte> remaining = _readOnlySequence.Slice(_position);
            if (remaining.Length > 0)
            {
                byte result = remaining.First.Span[0];
                _position = _readOnlySequence.GetPosition(1, _position);
                return result;
            }
            else
            {
                return -1;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }

    internal class BufferWriterStream : Stream
    {
        private readonly IBufferWriter<byte> _bufferWriter;

        private Memory<byte> _memory;
        private int _memoryUsed;

        public BufferWriterStream(IBufferWriter<byte> bufferWriter)
        {
            _bufferWriter = bufferWriter;
        }

        private void EnsureBuffer()
        {
            var remaining = _memory.Length - _memoryUsed;
            if (remaining == 0)
            {
                // Used up the memory from the buffer writer so advance and get more
                if (_memoryUsed > 0)
                {
                    _bufferWriter.Advance(_memoryUsed);
                }

                _memory = _bufferWriter.GetMemory();
                _memoryUsed = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Span<byte> GetBuffer()
        {
            EnsureBuffer();

            return _memory.Span.Slice(_memoryUsed, _memory.Length - _memoryUsed);
        }

        private void WriteInternal(ReadOnlySpan<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                // The destination byte array might not be large enough so multiple writes are sometimes required
                var destination = GetBuffer();

                buffer.CopyTo(destination);

                var written = Math.Min(destination.Length, buffer.Length);

                buffer = buffer.Slice(written);
                _memoryUsed += written;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteInternal(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            WriteInternal(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WriteInternal(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default(CancellationToken))
        {
            WriteInternal(buffer.Span);
            return default;
        }

        public override void WriteByte(byte value)
        {
            var destination = GetBuffer();

            destination[0] = value;
            _memoryUsed++;
        }

        public override void Flush()
        {
            if (_memoryUsed > 0)
            {
                _bufferWriter.Advance(_memoryUsed);
                _memory = _memory.Slice(_memoryUsed, _memory.Length - _memoryUsed);
                _memoryUsed = 0;
            }
        }

        #region Stream implementation
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }
        #endregion
    }

    internal class UnaryServerCallHandler<TRequest, TResponse, TService> : IServerCallHandler
        where TRequest : class
        where TResponse : class
        where TService : class
    {
        private readonly Method<TRequest, TResponse> _method;

        public UnaryServerCallHandler(Method<TRequest, TResponse> method)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
        }

        public async Task HandleCallAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = "application/grpc";
            httpContext.Response.Headers.Append("grpc-encoding", "identity");

            var input = httpContext.Request.BodyPipe;
            var result = await input.ReadAsync();
            var buffer = result.Buffer;

            var length = ReadHeader(buffer);

            var messageBuffer = buffer.Slice(5, length);

            var messageStream = new ReadOnlySequenceStream(messageBuffer);

            var request = (IMessage)Activator.CreateInstance<TRequest>();
            request.MergeFrom(new CodedInputStream(messageStream));

            input.AdvanceTo(messageBuffer.End);

            // TODO: make sure there are no more request messages.

            // Activate the implementation type via DI.
            var activator = httpContext.RequestServices.GetRequiredService<IGrpcServiceActivator<TService>>();
            var service = activator.Create();

            // Select procedure using reflection
            var handlerMethod = typeof(TService).GetMethod(_method.Name);

            // Invoke procedure
            var response = await (Task<TResponse>)handlerMethod.Invoke(service, new object[] { request, null });

            IMessage responseMessage = (IMessage)response;

            WriteHeader(httpContext.Response.BodyPipe, responseMessage.CalculateSize());

            BufferWriterStream bufferWriterStream = new BufferWriterStream(httpContext.Response.BodyPipe);
            var outputStream = new CodedOutputStream(bufferWriterStream);
            responseMessage.WriteTo(outputStream);
            outputStream.Flush();
            bufferWriterStream.Flush();

            await httpContext.Response.BodyPipe.FlushAsync();

            //_method.ResponseMarshaller.ContextualSerializer

            //IBufferWriter<byte> bufferWriter = new IBufferWriter<byte>();

            //StreamPipeWriter s;
            //StreamPipeReader streamPipeReader = new StreamPipeReader()
            //bufferWriter.GetSpan()

            //resposeMessage.WriteTo

            // TODO: make sure the response is not null
            //var serializationContext = new SerializationContext();
            //_method.ResponseMarshaller.ContextualSerializer(response, serializationContext);

            //await StreamUtils.WriteMessageAsync(httpContext.Response.Body, responsePayload, 0, responsePayload.Length);

            httpContext.Response.AppendTrailer("grpc-status", ((int)StatusCode.OK).ToString());
        }

        private void WriteHeader(IBufferWriter<byte> bufferWriter, int length)
        {
            const int headerSize = 1 + MessageDelimiterSize;

            var buffer = bufferWriter.GetSpan(headerSize);
            buffer[0] = 0;
            StreamUtils.EncodeMessageLength(length, buffer.Slice(1));

            bufferWriter.Advance(headerSize);
        }

        private const int MessageDelimiterSize = 4;  // how many bytes it takes to encode "Message-Length"


        private int ReadHeader(ReadOnlySequence<byte> buffer)
        {
            var span = buffer.First.Span;

            //var buffer.First.;
            var compressionFlag = span[0];
            var messageLength = DecodeMessageLength(span.Slice(1, 4));

            if (compressionFlag != 0)
            {
                // TODO(jtattermusch): support compressed messages
                throw new IOException("Compressed messages are not yet supported.");
            }

            return messageLength;
        }

        private static int DecodeMessageLength(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < MessageDelimiterSize)
            {
                throw new ArgumentException("Buffer too small to decode message length.");
            }

            ulong result = 0;
            for (int i = 0; i < MessageDelimiterSize; i++)
            {
                // msg length stored in big endian
                result = (result << 8) + buffer[i];
            }

            if (result > int.MaxValue)
            {
                throw new IOException("Message too large: " + result);
            }
            return (int)result;
        }
    }
}
