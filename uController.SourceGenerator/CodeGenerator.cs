﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;

namespace uController.CodeGeneration
{
    public class CodeGenerator
    {
        private readonly HttpModel _model;
        private readonly StringBuilder _codeBuilder = new StringBuilder();
        private readonly MetadataLoadContext _metadataLoadContext;
        private int _indent;

        public CodeGenerator(HttpModel model, MetadataLoadContext metadataLoadContext)
        {
            _model = model;
            _metadataLoadContext = metadataLoadContext;
        }

        public HashSet<Type> FromBodyTypes { get; set; } = new HashSet<Type>();

        // Pretty print the type name
        private string S(Type type) => TypeNameHelper.GetTypeDisplayName(type);

        private Type Unwrap(Type type)
        {
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                // instantiated generic type only
                Type genericType = type.GetGenericTypeDefinition();
                if (genericType.Equals(typeof(Nullable<>)))
                {
                    return type.GetGenericArguments()[0];
                }
            }
            return null;
        }

        public void Indent()
        {
            _indent++;
        }

        public void Unindent()
        {
            _indent--;
        }

        public string Generate()
        {
            Write($@"//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//     Runtime Version:{Environment.Version}
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------");
            WriteLine("");
            WriteLine("");
            var className = $"{_model.HandlerType.Name}RouteExtensions";
            var innerClassName = $"{_model.HandlerType.Name }Routes";
            WriteLine("using Microsoft.AspNetCore.Builder;");
            WriteLine("using Microsoft.Extensions.DependencyInjection;");
            WriteLine("");
            WriteLine($"namespace {_model.HandlerType.Namespace}");
            WriteLine("{");
            Indent();
            WriteLine($"public static class {className}");
            WriteLine("{");
            Indent();
            GenerateRoutes(innerClassName);

            // Generate the inner class
            WriteLine($"private class {innerClassName}");
            WriteLine("{"); // inner class start
            Indent();
            var ctors = _model.HandlerType.GetConstructors();
            if (ctors.Length > 1 || ctors[0].GetParameters().Length > 0)
            {
                WriteLine($"private readonly {typeof(ObjectFactory)} _factory = {typeof(ActivatorUtilities)}.CreateFactory(typeof({S(_model.HandlerType)}), {typeof(Type)}.EmptyTypes);");
                WriteLine("");
            }

            WriteLine($"private readonly {typeof(JsonRequestReader)} _requestReader = new {typeof(JsonRequestReader)}();");
            WriteLine("");

            foreach (var method in _model.Methods)
            {
                Generate(method);
            }
            Unindent();
            WriteLine("}"); //inner class end
            Unindent();
            WriteLine("}"); // outer class end
            Unindent();
            WriteLine("}"); // namespace end

            return _codeBuilder.ToString();
        }

        private void GenerateRoutes(string innerClassName)
        {
            // void IEndpointRouteProvider.MapHttpHandler<THttpHandler>(IEndpointRouteBuilder routes) where THttpHandler : HandlerType
            if (_model.Methods.Count == 0)
            {
                return;
            }

            // Make sure overloads work
            WriteLine($"public static {S(typeof(void))} MapHttpHandler<THttpHandler>(this {typeof(IEndpointRouteBuilder)} routes) where THttpHandler : {S(_model.HandlerType)}");
            WriteLine("{");
            Indent();
            WriteLine($"var handler = new {innerClassName}();");
            foreach (var method in _model.Methods)
            {
                Write($"routes.Map(\"{method.RoutePattern}\", handler.{method.UniqueName})");
                bool first = true;
                foreach (CustomAttributeData metadata in method.Metadata)
                {
                    if (first)
                    {
                        WriteNoIndent($".WithMetadata(");
                    }
                    else
                    {
                        WriteNoIndent(", ");
                    }

                    WriteNoIndent($"new {S(metadata.AttributeType)}()");
                    first = false;
                }
                WriteLineNoIndent(");");
            }
            Unindent();
            WriteLine("}");
        }

        private void Generate(MethodModel method)
        {
            // [DebuggerStepThrough]
            WriteLine($"[{typeof(DebuggerStepThroughAttribute)}]");

            var methodStartIndex = _codeBuilder.Length + 4 * _indent;
            WriteLine($"public async {typeof(Task)} {method.UniqueName}({typeof(HttpContext)} httpContext)");
            WriteLine("{");
            Indent();
            var ctors = _model.HandlerType.GetConstructors();
            if (ctors.Length > 1 || ctors[0].GetParameters().Length > 0)
            {
                // Lazy, defer to DI system if
                WriteLine($"var handler = ({S(_model.HandlerType)})_factory(httpContext.RequestServices, {typeof(Array)}.Empty<{typeof(object)}>());");
            }
            else
            {
                WriteLine($"var handler = new {S(_model.HandlerType)}();");
            }

            // Declare locals
            var hasAwait = false;
            var hasFromBody = false;
            var hasFromForm = false;
            foreach (var parameter in method.Parameters)
            {
                var parameterName = "arg_" + parameter.Name.Replace("_", "__");
                if (parameter.ParameterType.Equals(typeof(HttpContext)))
                {
                    WriteLine($"var {parameterName} = httpContext;");
                }
                else if (parameter.ParameterType.Equals(typeof(IFormCollection)))
                {
                    WriteLine($"var {parameterName} = await httpContext.Request.ReadFormAsync();");
                    hasAwait = true;
                }
                else if (parameter.FromRoute != null)
                {
                    GenerateConvert(parameterName, parameter.ParameterType, parameter.FromRoute, "httpContext.Request.RouteValues", nullable: true);
                }
                else if (parameter.FromQuery != null)
                {
                    GenerateConvert(parameterName, parameter.ParameterType, parameter.FromQuery, "httpContext.Request.Query");
                }
                else if (parameter.FromHeader != null)
                {
                    GenerateConvert(parameterName, parameter.ParameterType, parameter.FromHeader, "httpContext.Request.Headers");
                }
                else if (parameter.FromServices)
                {
                    WriteLine($"var {parameterName} = httpContext.RequestServices.GetRequiredService<{S(parameter.ParameterType)}>();");
                }
                else if (parameter.FromForm != null)
                {
                    if (!hasFromForm)
                    {
                        WriteLine($"var formCollection = await httpContext.Request.ReadFormAsync();");
                        hasAwait = true;
                        hasFromForm = true;
                    }
                    GenerateConvert(parameterName, parameter.ParameterType, parameter.FromForm, "formCollection");
                }
                else if (parameter.FromBody)
                {
                    if (!hasFromBody)
                    {
                        WriteLine($"var reader = httpContext.RequestServices.GetService<{typeof(IHttpRequestReader)}>() ?? _requestReader;");
                        hasFromBody = true;
                    }

                    if (!parameter.ParameterType.Equals(typeof(JsonElement)))
                    {
                        FromBodyTypes.Add(parameter.ParameterType);
                    }

                    WriteLine($"var {parameterName} = ({S(parameter.ParameterType)})await reader.ReadAsync(httpContext, typeof({S(parameter.ParameterType)}));");
                    hasAwait = true;
                }
            }

            AwaitableInfo awaitableInfo = default;
            // Populate locals
            if (method.MethodInfo.ReturnType.Equals(typeof(void)))
            {
                Write("");
            }
            else
            {
                if (AwaitableInfo.IsTypeAwaitable(method.MethodInfo.ReturnType, out awaitableInfo))
                {
                    if (awaitableInfo.ResultType.Equals(typeof(void)))
                    {
                        Write("await ");
                    }
                    else
                    {
                        Write("var result = await ");
                    }

                    hasAwait = true;
                }
                else
                {
                    Write("var result = ");
                }
            }
            WriteNoIndent($"handler.{method.MethodInfo.Name}(");
            bool first = true;
            foreach (var parameter in method.Parameters)
            {
                var parameterName = "arg_" + parameter.Name.Replace("_", "__");
                if (!first)
                {
                    WriteNoIndent(", ");
                }
                WriteNoIndent(parameterName);
                first = false;
            }
            WriteLineNoIndent(");");

            if (!hasAwait)
            {
                // Remove " async" from method signature.
                _codeBuilder.Remove(methodStartIndex + 6, 6);
            }

            void AwaitOrReturn(string executeAsync)
            {
                if (hasAwait)
                {
                    Write("await ");
                }
                else
                {
                    Write("return ");
                }

                WriteLineNoIndent(executeAsync);
            }

            var unwrappedType = awaitableInfo.ResultType ?? method.MethodInfo.ReturnType;
            if (_metadataLoadContext.Resolve<IResult>().IsAssignableFrom(unwrappedType))
            {
                AwaitOrReturn("result.ExecuteAsync(httpContext);");
            }
            else if (!unwrappedType.Equals(typeof(void)))
            {
                AwaitOrReturn($"new {typeof(ObjectResult)}(result).ExecuteAsync(httpContext);");
            }
            else if (!hasAwait)
            {
                WriteLine($"return {typeof(Task)}.{nameof(Task.CompletedTask)};");
            }

            Unindent();
            WriteLine("}");
            WriteLine("");
        }

        private void GenerateConvert(string sourceName, Type type, string key, string sourceExpression, bool nullable = false)
        {
            if (type.Equals(typeof(string)))
            {
                WriteLine($"var {sourceName} = {sourceExpression}[\"{key}\"]" + (nullable ? "?.ToString();" : ".ToString();"));
            }
            else
            {
                WriteLine($"var {sourceName}_Value = {sourceExpression}[\"{key}\"]" + (nullable ? "?.ToString();" : ".ToString();"));
                WriteLine($"{S(type)} {sourceName};");

                // TODO: Handle cases where TryParse isn't available
                // type = Unwrap(type) ?? type;
                var unwrappedType = Unwrap(type);
                if (unwrappedType == null)
                {
                    // Type isn't nullable
                    WriteLine($"if ({sourceName}_Value == null || !{S(type)}.TryParse({sourceName}_Value, out {sourceName}))");
                    WriteLine("{");
                    Indent();
                    WriteLine($"{sourceName} = default;");
                    Unindent();
                    WriteLine("}");
                }
                else
                {
                    WriteLine($"if ({sourceName}_Value != null && {S(unwrappedType)}.TryParse({sourceName}_Value, out var {sourceName}_Temp))");
                    WriteLine("{");
                    Indent();
                    WriteLine($"{sourceName} = {sourceName}_Temp;");
                    Unindent();
                    WriteLine("}");
                    WriteLine("else");
                    WriteLine("{");
                    Indent();
                    WriteLine($"{sourceName} = default;");
                    Unindent();
                    WriteLine("}");

                }
            }
        }

        private void WriteLineNoIndent(string value)
        {
            _codeBuilder.AppendLine(value);
        }

        private void WriteNoIndent(string value)
        {
            _codeBuilder.Append(value);
        }

        private void Write(string value)
        {
            if (_indent > 0)
            {
                _codeBuilder.Append(new string(' ', _indent * 4));
            }
            _codeBuilder.Append(value);
        }

        private void WriteLine(string value)
        {
            if (_indent > 0)
            {
                _codeBuilder.Append(new string(' ', _indent * 4));
            }
            _codeBuilder.AppendLine(value);
        }
    }
}
