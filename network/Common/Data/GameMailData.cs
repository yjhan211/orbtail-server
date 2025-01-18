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
using network.common.data.models;
using network.managers;

namespace network.common.data
{
    public static class GameMailData
    {
        private static readonly Dictionary<int, MailInfoData> Mails = new();
        public static void Initialize(List<CsvRow> csvData)
        {
            var mailInfos = csvData.Select(MailInfoData.CreateFromData);
            foreach (var mailInfo in mailInfos) Mails[mailInfo.Id] = mailInfo;
        }
        
        public static MailInfoData Get(int id)
        {
            if (!Mails.TryGetValue(id, out var mail)) throw new KeyNotFoundException($"Quest {id} not found");
            return mail;
        }

        public static void Validate(LogManager logManager)
        {
            logManager.WriteDebugLog("=== GameMailData Validation ===");
            foreach (var (id, mail) in Mails)
            {
                logManager.WriteDebugLog($"[{id}]{mail.From}: {mail.Comment}");
            }
            logManager.WriteDebugLog("All validations passed successfully!");
        }
    }
    
    public class MailInfoData
    {
        public int Id { get; private set; }
        public string From { get; private set; }
        public string Comment { get; private set; }
        
        public static MailInfoData CreateFromData(CsvRow row)
        {
            return new MailInfoData
            {
                Id = int.Parse(row["id"]),
                From = row["from"],
                Comment = row["comment"],
            };
        }
    }
}