// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Newtonsoft.Json;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    public class LocalizedText
    {
        public LocalizedText(string jsonString)
        {
            try
            {
                jsonString = jsonString.Trim('"');
                jsonString = jsonString.Replace("\"\"", "\"");

                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonString);
                Kr = dict!["kr"];
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Failed to parse LocalizedText. Raw input: '{jsonString}'", ex);
            }
        }

        public string Kr { get; }

        public override string ToString()
        {
            return Kr;
        }
    }
}
