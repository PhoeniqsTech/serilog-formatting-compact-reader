// Copyright 2013-2015 Serilog Contributors
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
using Newtonsoft.Json;

namespace Serilog.Formatting.Compact.Reader
{
    class TextException : Exception
    {
        public TextException(){}

        public override string ToString()
        {
            return StackTrace;
        }
        [JsonIgnore]
        public string RealStackTrace { get; set; }
        public override string StackTrace { get { return RealStackTrace; } }
        [JsonIgnore]
        public string RealMessage { get; set; }
        public override string Message { get { return RealMessage; } }

        [JsonIgnore]
        public string RealSource { get; set; }
        public override string Source { get { return RealSource; } }

        public string TypeName { get; set; }
    }
}
