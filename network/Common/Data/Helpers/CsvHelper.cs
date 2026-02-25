// ReSharper disable All
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#endif

namespace network.common.data.helpers
{
    public static class CsvHelper
    {
        public static List<CsvRow> LoadCsv(string filePath)
        {
            string[] lines;

#if UNITY_ANDROID && !UNITY_EDITOR
            // 안드로이드 환경: Resources 폴더에서 직접 읽기
            var textAsset = Resources.Load<TextAsset>(filePath);
            if (textAsset == null)
                throw new FileNotFoundException($"CSV file not found in Resources: {filePath}");

            lines = textAsset.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Resources.UnloadAsset(textAsset);
#else
            // 서버 및 에디터 환경: 파일 시스템에서 직접 읽기
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"CSV file not found: {filePath}");

            lines = File.ReadAllLines(filePath, Encoding.UTF8);
#endif

            var result = new List<CsvRow>();

            // 빈 줄 필터링
            var validLines = new List<string>();
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    validLines.Add(line.Trim());
            }

            if (validLines.Count == 0)
                throw new InvalidDataException("CSV file is empty or contains no valid data");

            // Parse header
            var headers = ParseCsvLine(validLines[0]);

            // Parse data rows
            for (var i = 1; i < validLines.Count; i++)
            {
                var values = ParseCsvLine(validLines[i]);
                if (values.Length != headers.Length)
                    throw new InvalidDataException(
                        $"Line {i + 1}: Column count mismatch. Expected {headers.Length}, got {values.Length}");

                var row = new CsvRow(headers, values);
                result.Add(row);
            }

            return result;
        }

        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var currentField = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // "" 이스케이프 처리: 인용 내부의 연속 따옴표는 리터럴 " 하나로 변환
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            currentField.Append('"');
                            i++; // 다음 따옴표 건너뜀
                        }
                        else
                        {
                            inQuotes = false; // 닫는 따옴표
                        }
                    }
                    else
                    {
                        currentField.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true; // 여는 따옴표
                    }
                    else if (c == ',')
                    {
                        fields.Add(currentField.ToString());
                        currentField.Clear();
                    }
                    else
                    {
                        currentField.Append(c);
                    }
                }
            }

            // 마지막 필드 추가
            fields.Add(currentField.ToString());

            return fields.ToArray();
        }
    }

    public class CsvRow
    {
        private readonly string[] _headers;
        private readonly string[] _values;

        public CsvRow(string[] headers, string[] values)
        {
            _headers = headers;
            _values = values;
        }

        public string this[string columnName]
        {
            get
            {
                var index = Array.IndexOf(_headers, columnName);
                if (index == -1)
                    throw new KeyNotFoundException($"Column '{columnName}' not found");
                return _values[index].Trim();
            }
        }

        public bool ContainsKey(string columnName)
        {
            return Array.IndexOf(_headers, columnName) != -1;
        }
    }
}
