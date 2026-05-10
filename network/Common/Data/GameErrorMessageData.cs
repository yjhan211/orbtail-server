// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System.Collections.Generic;
using network.common;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameErrorMessageData
    {
        private static Dictionary<ErrorCode, LocalizedText> _messages = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            _messages.Clear();

            foreach (var row in csvData)
            {
                var code = (ErrorCode)int.Parse(row["code"]);
                var message = LocalizedText.FromCsv(row, "message");
                _messages[code] = message;
            }
        }

        /// <summary>
        /// 에러 코드에 해당하는 메시지 반환
        /// </summary>
        public static string Get(ErrorCode errorCode)
        {
            return Get(errorCode, null);
        }

        /// <summary>
        /// 에러 코드에 해당하는 메시지를 지정 언어로 반환
        /// </summary>
        public static string Get(ErrorCode errorCode, string lang)
        {
            if (_messages.TryGetValue(errorCode, out var message))
            {
                return string.IsNullOrEmpty(lang) ? message.Kr : message.Get(lang);
            }

            return $"오류가 발생했습니다. (코드: {(int)errorCode})";
        }
    }
}
