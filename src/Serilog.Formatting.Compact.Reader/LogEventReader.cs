﻿// Copyright 2013-2015 Serilog Contributors
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

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using Serilog.Parsing;

namespace Serilog.Formatting.Compact.Reader
{
    /// <summary>
    /// Reads files produced by <em>Serilog.Formatting.Compact.CompactJsonFormatter</em>. Events
    /// are expected to be encoded as newline-separated JSON documents.
    /// </summary>
    public class LogEventReader : IDisposable
    {
        static readonly MessageTemplateParser Parser = new MessageTemplateParser();
        static readonly Rendering[] NoRenderings = new Rendering[0];
        readonly TextReader _text;
        readonly JsonSerializer _serializer;

        int _lineNumber;

        /// <summary>
        /// Construct a <see cref="LogEventReader"/>.
        /// </summary>
        /// <param name="text">Text to read from.</param>
        /// <param name="serializer">If specified, a JSON serializer used when converting event documents.</param>
        public LogEventReader(TextReader text, JsonSerializer serializer = null)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _serializer = serializer ?? CreateSerializer();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _text.Dispose();
        }

        /// <summary>
        /// Read a line from the input. Blank lines are skipped.
        /// </summary>
        /// <param name="evt"></param>
        /// <returns>True if an event could be read; false if the end-of-file was encountered.</returns>
        /// <exception cref="InvalidDataException">The data format is invalid.</exception>
        public bool TryRead(out LogEvent evt)
        {
            var line = _text.ReadLine();
            _lineNumber++;
            while (string.IsNullOrWhiteSpace(line))
            {
                if (line == null)
                {
                    evt = null;
                    return false;
                }
                line = _text.ReadLine();
                _lineNumber++;
            }

            var data = _serializer.Deserialize(new JsonTextReader(new StringReader(line)));
            var fields = data as JObject;
            if (fields == null)
                throw new InvalidDataException($"The data on line {_lineNumber} is not a complete JSON object.");

            evt = ReadFromJObject(_lineNumber, fields);
            return true;
        }

        /// <summary>
        /// Read a single log event from a JSON-encoded document.
        /// </summary>
        /// <param name="document">The event in compact-JSON.</param>
        /// <param name="serializer">If specified, a JSON serializer used when converting event documents.</param>
        /// <returns>The log event.</returns>
        public static LogEvent ReadFromString(string document, JsonSerializer serializer = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            serializer = serializer ?? CreateSerializer();
            var jObject = serializer.Deserialize<JObject>(new JsonTextReader(new StringReader(document)));
            return ReadFromJObject(jObject);

        }

        /// <summary>
        /// Read a single log event from an already-deserialized JSON object.
        /// </summary>
        /// <param name="jObject">The deserialized compact-JSON event.</param>
        /// <returns>The log event.</returns>
        public static LogEvent ReadFromJObject(JObject jObject)
        {
            if (jObject == null) throw new ArgumentNullException(nameof(jObject));
            return ReadFromJObject(1, jObject);
        }

        static LogEvent ReadFromJObject(int lineNumber, JObject jObject)
        {
            var timestamp = GetRequiredTimestampField(lineNumber, jObject, ClefFields.Timestamp);

            string messageTemplate;
            if (TryGetOptionalField(lineNumber, jObject, ClefFields.MessageTemplate, out var mt))
                messageTemplate = mt;
            else if (TryGetOptionalField(lineNumber, jObject, ClefFields.Message, out var m))
                messageTemplate = MessageTemplateSyntax.Escape(m);
            else
                messageTemplate = null;

            var level = LogEventLevel.Information;
            if (TryGetOptionalField(lineNumber, jObject, ClefFields.Level, out string l))
                level = (LogEventLevel)Enum.Parse(typeof(LogEventLevel), l);
            TextException exception = null;
            if (TryGetOptionalField(lineNumber, jObject, ClefFields.Exception, out string ex))
                exception = new TextException()
                {
                    RealStackTrace = ex
                };
            if (TryGetExceptionDetails(jObject, out string exMessage, out string source, out string type))
            {
                exception.RealMessage = exMessage;
                exception.RealSource = source;
                exception.TypeName = type;
            }
            var parsedTemplate = messageTemplate == null ?
                new MessageTemplate(Enumerable.Empty<MessageTemplateToken>()) :
                Parser.Parse(messageTemplate);

            var renderings = NoRenderings;

            if (jObject.TryGetValue(ClefFields.Renderings, out JToken r))
            {
                var renderedByIndex = r as JArray;
                if (renderedByIndex == null)
                    throw new InvalidDataException($"The `{ClefFields.Renderings}` value on line {lineNumber} is not an array as expected.");

                renderings = parsedTemplate.Tokens
                    .OfType<PropertyToken>()
                    .Where(t => t.Format != null)
                    .Zip(renderedByIndex, (t, rd) => new Rendering(t.PropertyName, t.Format, rd.Value<string>()))
                    .ToArray();
            }

            var properties = jObject
                .Properties()
                .Where(f => !ClefFields.All.Contains(f.Name))
                .Select(f =>
                {
                    var name = ClefFields.Unescape(f.Name);
                    var renderingsByFormat = renderings.Length != 0 ? renderings.Where(rd => rd.Name == name).ToArray() : NoRenderings;
                    return PropertyFactory.CreateProperty(name, f.Value, renderingsByFormat);
                })
                .ToList();

            if (TryGetOptionalEventId(lineNumber, jObject, ClefFields.EventId, out var eventId))
            {
                properties.Add(new LogEventProperty("@i", new ScalarValue(eventId)));
            }

            return new LogEvent(timestamp, level, exception, parsedTemplate, properties);
        }
        static bool TryGetExceptionDetails(JObject data, out string message, out string source, out string type)
        {
            bool val = false;
            if (!data.TryGetValue(ClefFields.ExceptionDetail, out JToken token) && token?.Type != JTokenType.Object)
            {
                message = null;
                source = null;
                type = null;
            }
            else
            {
                ((JObject)token).TryGetValue("Message", out JToken messageToken);
                message = messageToken.Value<string>();

                ((JObject)token).TryGetValue("Source", out JToken sourceToken);
                source = sourceToken.Value<string>();

                ((JObject)token).TryGetValue("Type", out JToken typeToken);
                type = typeToken.Value<string>();
                val = true;
            }
            return val;
        }
        static bool TryGetOptionalField(int lineNumber, JObject data, string field, out string value)
        {
            JToken token;
            if (!data.TryGetValue(field, out token) || token.Type == JTokenType.Null)
            {
                value = null;
                return false;
            }

            if (token.Type != JTokenType.String)
                throw new InvalidDataException($"The value of `{field}` on line {lineNumber} is not in a supported format.");

            value = token.Value<string>();
            return true;
        }

        static bool TryGetOptionalEventId(int lineNumber, JObject data, string field, out object eventId)
        {
            JToken token;
            if (!data.TryGetValue(field, out token) || token.Type == JTokenType.Null)
            {
                eventId = null;
                return false;
            }

            switch (token.Type)
            {
                case JTokenType.String:
                    eventId = token.Value<string>();
                    return true;
                case JTokenType.Integer:
                    eventId = token.Value<uint>();
                    return true;
                default:
                    throw new InvalidDataException(
                        $"The value of `{field}` on line {lineNumber} is not in a supported format.");
            }
        }

        static DateTimeOffset GetRequiredTimestampField(int lineNumber, JObject data, string field)
        {
            JToken token;
            if (!data.TryGetValue(field, out token) || token.Type == JTokenType.Null)
                throw new InvalidDataException($"The data on line {lineNumber} does not include the required `{field}` field.");

            if (token.Type == JTokenType.Date)
            {
                var dt = token.Value<JValue>().Value;
                if (dt is DateTimeOffset)
                    return (DateTimeOffset)dt;

                return (DateTime)dt;
            }

            if (token.Type != JTokenType.String)
                throw new InvalidDataException($"The value of `{field}` on line {lineNumber} is not in a supported format.");

            var text = token.Value<string>();
            return DateTimeOffset.Parse(text);
        }

        static JsonSerializer CreateSerializer()
        {
            return JsonSerializer.Create(new JsonSerializerSettings
            {
                DateParseHandling = DateParseHandling.None,
                Culture = CultureInfo.InvariantCulture
            });
        }
    }
}
