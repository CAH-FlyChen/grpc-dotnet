﻿#region Copyright notice and license

// Copyright 2015 gRPC authors.
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
using System.Collections.Generic;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Grpc.AspNetCore.Server.Internal
{
    internal class GrpcServiceBinder<TService> : ServiceBinderBase where TService : class
    {
        private readonly IEndpointRouteBuilder _builder;

        internal IList<IEndpointConventionBuilder> EndpointConventionBuilders { get; } = new List<IEndpointConventionBuilder>();

        internal GrpcServiceBinder(IEndpointRouteBuilder builder)
        {
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        }

        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method)
        {
            IServerCallHandler handler;

            switch (method.Type)
            {
                case MethodType.Unary:
                    handler = new UnaryServerCallHandler<TRequest, TResponse, TService>(method);
                    break;
                case MethodType.ClientStreaming:
                    handler = new ClientStreamingServerCallHandler<TRequest, TResponse, TService>(method);
                    break;
                case MethodType.ServerStreaming:
                    handler = new ServerStreamingServerCallHandler<TRequest, TResponse, TService>(method);
                    break;
                case MethodType.DuplexStreaming:
                    handler = new DuplexStreamingServerCallHandler<TRequest, TResponse, TService>(method);
                    break;
                default:
                    throw new NotImplementedException();
            }

            EndpointConventionBuilders.Add(_builder.MapPost(method.FullName, handler.HandleCallAsync));
        }
    }
}
