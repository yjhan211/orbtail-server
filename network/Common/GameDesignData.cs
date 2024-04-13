namespace network
{
    // TODO CSV
    public static class GameDesignData
    {
        public static (string, string, string, string, string) GetItemDetail(int item_id)
        {
            var result = ("", "", "", "", "");
            switch (item_id)
            {
                case 101000001:
                    result = ("수습 연구원의 머리", "조건 없음", "", "100", "초심자의 결의는 언제나 반짝여요.");
                    break;

                case 101000002:
                    result = ("지질학자의 머리", "조건 없음", "", "100", "사실 돌을 좋아한다기 보단 돌이 되고 싶었어요.");
                    break;

                case 102000001:
                    result = (
                        "수습 지질학자의 모자",
                        "수습 지질학자 이상",
                        "채광 Lv1 사용 가능",
                        "100",
                        "지질학 입문자의 든든한 파트너"
                    );
                    break;

                case 102000002:
                    result = (
                        "수습 식물학자의 모자",
                        "수습 식물학자 이상",
                        "채집 Lv1 사용 가능",
                        "100",
                        "식물학 입문자의 든든한 파트너"
                    );
                    break;

                case 102000003:
                    result = (
                        "지질학자의 모자",
                        "지질학자 이상",
                        "채광 Lv2 사용 가능",
                        "100",
                        "이제 연구자 태가 좀 나는 것 같아요."
                    );
                    break;

                case 102000004:
                    result = (
                        "식물학자의 모자",
                        "식물학자 이상",
                        "채집 Lv2 사용 가능",
                        "100",
                        "이제 연구자 태가 좀 나는 것 같아요."
                    );
                    break;

                case 103000001:
                    result = (
                        "낡은 탐험복",
                        "연구원 이상",
                        "조사 Lv1 사용 가능",
                        "100",
                        "낡았지만 부담없이 입을 수 있어 오히려 좋아요."
                    );
                    break;

                case 201000001:
                    result = ("밤양갱", "조건 없음", "컨디션 20 회복", "", "우리는 너무 많이 생각하고는 해요.");
                    break;

                case 301000001:
                    result = ("조약돌", "제작 재료", "", "", "재료보다는 수집품으로 인기가 많아요.");
                    break;

                case 301000002:
                    result = ("잡초", "제작 재료", "", "", "잡초도 약에 쓰려면 없다는 말이 있어요.");
                    break;

                case 301000003:
                    result = ("점토암", "제작 재료", "", "", "물과 혼합하면 쉽게 모양을 변형할 수 있어요.");
                    break;

                case 301000004:
                    result = ("미나리", "제작 재료", "", "", "물가나 습지에서 잘 자라는 전통 식재료.");
                    break;

                case 301000005:
                    result = ("실트암", "제작 재료", "", "", "단단하지는 않지만 가공이 쉽고 다루기 편해요.");
                    break;

                case 301000006:
                    result = ("민들레", "제작 재료", "", "", "뿌리가 깊고 번식력이 강해요.");
                    break;

                case 301000007:
                    result = ("돌구슬", "제작 재료", "", "", "경쾌한 소리가 나요.");
                    break;

                case 301000008:
                    result = ("건초", "제작 재료", "", "", "정성스럽게 수분을 바싹 말렸어요.");
                    break;
            }

            return result;
        }

        public static (string, int, PlayerState) GetSkillDetail(int skill_id)
        {
            var result = ("", 0, PlayerState.NONE);
            switch (skill_id)
            {
                case 10001:
                    result = ("채광 Lv1: 암석으로부터 자원을 얻는 기술", 1, PlayerState.GEO_WORK_1);
                    break;

                case 20001:
                    result = ("채집 Lv1: 식물로부터 자원을 얻는 기술", 1, PlayerState.BOTAN_WORK_1);
                    break;

                case 10002:
                    result = ("채광 Lv2: 암석으로부터 자원을 얻는 기술", 2, PlayerState.GEO_WORK_1);
                    break;

                case 20002:
                    result = ("채집 Lv2: 식물로부터 자원을 얻는 기술", 2, PlayerState.BOTAN_WORK_1);
                    break;

                case 100001:
                    result = ("조사 Lv1: 자원의 가능성을 발견하는 기술", 1, PlayerState.RESEARCH_1);
                    break;

                case 200001:
                    result = ("제작: 연구 일지를 기반으로 제작하는 기술", 0, PlayerState.NONE);
                    break;
            }

            return result;
        }

        public static string GetJobDetail(JobType job_type)
        {
            string result = "";
            switch (job_type)
            {
                case JobType.GEOIOGIST:
                    result = "암석을 수집하고 분석하는 연구원";
                    break;

                case JobType.BOTANIST:
                    result = "식물을 채집하고 분석하는 연구원";
                    break;
            }

            return result;
        }

        public static string GetJobGradeDetail(JobGrade job_grade)
        {
            string result = "";
            switch (job_grade)
            {
                case JobGrade.TRAINEE:
                    result = "열정 가득한 수습 연구원";
                    break;
                case JobGrade.RESEARCHER:
                    result = "수습 단계를 통과한 정식 연구원";
                    break;
                case JobGrade.ASSOCIATE:
                    result = "연구 분야에 대한 전문성을 갖춘 주임 연구원";
                    break;
                case JobGrade.SENIOR_ASSOCIATE:
                    result = "높은 수준의 연구 능력을 보유한 선임 연구원";
                    break;
                case JobGrade.PRINCIPAL:
                    result = "연구의 권위자로 인정받는 책임 연구원";
                    break;
                case JobGrade.LEAD:
                    result = "학계 전체를 이끄는 수석 연구원";
                    break;
                case JobGrade.CHIEF:
                    result = "최고 수준의 연구 역량을 갖춘 대가";
                    break;
            }
            return result;
        }

        public static (string, int, JobType, int, string, List<int>) GetJobResourceDetail(
            int job_resource_id
        )
        {
            var reward_item_list = new List<int>();
            var result = ("", 0, JobType.NONE, 0, "", reward_item_list);
            switch (job_resource_id)
            {
                case 10001:
                    reward_item_list.Add(301000001);
                    result = ("암석", 20, JobType.GEOIOGIST, 1, "채광 Lv1 스킬이 필요해요.", reward_item_list);
                    break;

                case 10002:
                    reward_item_list.Add(301000001);
                    reward_item_list.Add(301000003);
                    reward_item_list.Add(301000005);
                    result = ("이암", 20, JobType.GEOIOGIST, 2, "채광 Lv2 스킬이 필요해요.", reward_item_list);
                    break;

                case 20001:
                    reward_item_list.Add(301000002);
                    result = ("들풀", 20, JobType.BOTANIST, 1, "채집 Lv1 스킬이 필요해요.", reward_item_list);
                    break;

                case 20002:
                    reward_item_list.Add(301000002);
                    reward_item_list.Add(301000004);
                    reward_item_list.Add(301000006);
                    result = ("산나물", 20, JobType.BOTANIST, 2, "채집 Lv2 스킬이 필요해요.", reward_item_list);
                    break;
            }

            return result;
        }

        // TODO 일단 하드코딩
        // 원래 resource_id보다는 반드시 높으며 job_id보다는 높아지지 않음 .. 으로 바꿀 것
        public static List<int> GetJobResourceUpgradePool(int job_resource_id)
        {
            var result = new List<int>();
            switch (job_resource_id)
            {
                case 10001:
                    result.Add(10002);
                    break;

                case 20001:
                    result.Add(20002);
                    break;
            }

            return result;
        }

        public static bool IsSameTypeItem(int item_id, int target_item_id)
        {
            return GetItemType(item_id) == GetItemType(target_item_id);
        }

        public static int GetItemType(int item_id)
        {
            int item_type = (int)(item_id / 1000000);
            return item_type;
        }

        public static List<(string, string)> GetWearItemSpriteInfo(int item_id)
        {
            int item_type = (int)(item_id / 1000000);
            var label_number = (int)(item_id % 1000000);

            List<(string, string)> result = new();
            switch (item_type)
            {
                case 101:
                    result.Add(("hair", $"hair_{label_number}"));
                    break;

                case 102:
                    result.Add(("hat", $"hat_{label_number}"));
                    break;

                case 103:
                    result.Add(("body", $"body_{label_number}"));
                    result.Add(("left_arm", $"left_arm_{label_number}"));
                    result.Add(("right_arm", $"right_arm_{label_number}"));
                    break;
            }

            return result;
        }

        public static (string, string) GetJobToolSpriteInfo(PlayerState player_state)
        {
            (string, string) result = ("tool", "none");
            switch (player_state)
            {
                case PlayerState.GEO_WORK_1:
                    result = ("tool", "tool_1");
                    break;

                case PlayerState.BOTAN_WORK_1:
                    result = ("tool", "tool_2");
                    break;

                case PlayerState.RESEARCH_1:
                    result = ("tool", "tool_3");
                    break;
            }

            return result;
        }

        public static int GetMaxHP(JobGrade job_grade)
        {
            var result = 0;
            switch (job_grade)
            {
                case JobGrade.TRAINEE:
                    result = 100;
                    break;

                case JobGrade.RESEARCHER:
                    result = 150;
                    break;

                case JobGrade.ASSOCIATE:
                    result = 200;
                    break;

                case JobGrade.SENIOR_ASSOCIATE:
                    result = 300;
                    break;

                case JobGrade.PRINCIPAL:
                    result = 500;
                    break;

                case JobGrade.LEAD:
                    result = 1000;
                    break;

                case JobGrade.CHIEF:
                    result = 10000;
                    break;
            }

            return result;
        }

        public static int GetMaxExp(JobGrade job_grade)
        {
            var result = 0;
            switch (job_grade)
            {
                case JobGrade.TRAINEE:
                    result = 100;
                    break;

                case JobGrade.RESEARCHER:
                    result = 1000;
                    break;

                case JobGrade.ASSOCIATE:
                    result = 10000;
                    break;

                case JobGrade.SENIOR_ASSOCIATE:
                    result = 100000;
                    break;

                case JobGrade.PRINCIPAL:
                    result = 1000000;
                    break;

                case JobGrade.LEAD:
                    result = 100000000;
                    break;

                case JobGrade.CHIEF:
                    result = 0;
                    break;
            }

            return result;
        }

        public static int GetSkill(int item_id)
        {
            int result = 0;
            switch (item_id)
            {
                case 102000001: // 지질학자의 모자
                    result = 10001; // 채광 레벨 1
                    break;

                case 102000002: // 식물학자의 모자
                    result = 20001; // 채집 레벨 1
                    break;

                case 102000003: // 식물학자의 모자
                    result = 10002; // 채광 레벨 2
                    break;

                case 102000004: // 식물학자의 모자
                    result = 20002; // 채광 레벨 2
                    break;

                case 103000001:
                    result = 100001; // 조사 레벨 1
                    break;
            }

            return result;
        }

        public static bool IsWearableItem(int item_id)
        {
            int item_type = (int)(item_id / 1000000);
            int kind = (int)(item_type / 100);

            var wearable = kind == 1;
            return wearable;
        }

        public static bool IsWearableJobInfo(int item_id, JobType job_type, JobGrade job_grade)
        {
            JobType target_job_type = JobType.NONE;
            JobGrade target_job_grade = JobGrade.NONE;
            switch (item_id)
            {
                case 102000001:
                    target_job_type = JobType.GEOIOGIST;
                    target_job_grade = JobGrade.TRAINEE;
                    break;

                case 102000003:
                    target_job_type = JobType.GEOIOGIST;
                    target_job_grade = JobGrade.RESEARCHER;
                    break;

                case 102000002:
                    target_job_type = JobType.BOTANIST;
                    target_job_grade = JobGrade.TRAINEE;
                    break;

                case 102000004:
                    target_job_type = JobType.BOTANIST;
                    target_job_grade = JobGrade.RESEARCHER;
                    break;

                case 103000001:
                    target_job_grade = JobGrade.RESEARCHER;
                    break;

                default:
                    return true;
            }

            if (target_job_type == JobType.NONE)
            {
                target_job_type = job_type;
            }

            return target_job_type == job_type && target_job_grade <= job_grade;
        }

        public static bool IsWearableJobGrade(int item_id, JobGrade grade)
        {
            JobGrade job_grade = JobGrade.TRAINEE;
            switch (item_id)
            {
                case 103000001:
                    job_grade = JobGrade.RESEARCHER;
                    break;
            }

            return job_grade <= grade;
        }

        public static bool IsUseableItem(int item_id)
        {
            int item_type = (int)(item_id / 1000000);
            int kind = (int)(item_type / 100);

            var useable = kind == 2;
            return useable;
        }

        public static int GetMaxItemCount(PlayerGrade grade)
        {
            var result = 0;

            switch (grade)
            {
                case PlayerGrade.RUFFIAN:
                    result = 5;
                    break;

                case PlayerGrade.LAW_BREAKER:
                    result = 10;
                    break;

                case PlayerGrade.COMMONER:
                    result = 20;
                    break;

                case PlayerGrade.LAW_ABIDING:
                    result = 30;
                    break;

                case PlayerGrade.RIGHTEOUS_PERSON:
                    result = 40;
                    break;

                case PlayerGrade.HERO:
                    result = 50;
                    break;

                case PlayerGrade.SAINT:
                    result = 100;
                    break;
            }

            return result;
        }

        public static string ConvertGrade(PlayerGrade grade)
        {
            var result = "";

            switch (grade)
            {
                case PlayerGrade.RUFFIAN:
                    result = "불량배";
                    break;

                case PlayerGrade.LAW_BREAKER:
                    result = "위법자";
                    break;

                case PlayerGrade.COMMONER:
                    result = "시민";
                    break;

                case PlayerGrade.LAW_ABIDING:
                    result = "준법자";
                    break;

                case PlayerGrade.RIGHTEOUS_PERSON:
                    result = "의인";
                    break;

                case PlayerGrade.HERO:
                    result = "영웅";
                    break;

                case PlayerGrade.SAINT:
                    result = "성자";
                    break;
            }

            return result;
        }

        public static string ConvertJobGrade(JobGrade job_grade)
        {
            var result = "";

            switch (job_grade)
            {
                case JobGrade.TRAINEE:
                    result = " 수습 연구원";
                    break;

                case JobGrade.RESEARCHER:
                    result = " 연구원";
                    break;

                case JobGrade.ASSOCIATE:
                    result = " 주임 연구원";
                    break;

                case JobGrade.SENIOR_ASSOCIATE:
                    result = " 선임 연구원";
                    break;

                case JobGrade.PRINCIPAL:
                    result = " 책임 연구원";
                    break;

                case JobGrade.LEAD:
                    result = " 수석 연구원";
                    break;

                case JobGrade.CHIEF:
                    result = "의 대가";
                    break;
            }

            return result;
        }

        public static string ConvertLabGrade(LabGrade lab_grade)
        {
            var result = "";

            switch (lab_grade)
            {
                case LabGrade.ALONE:
                    result = "개인 동아리";
                    break;

                case LabGrade.CLUB:
                    result = "동아리";
                    break;
            }

            return result;
        }

        public static int GetMaxLabMemberNum(LabGrade lab_grade)
        {
            var result = 0;

            switch (lab_grade)
            {
                case LabGrade.ALONE:
                    result = 1;
                    break;

                case LabGrade.CLUB:
                    result = 2;
                    break;
            }

            return result;
        }

        public static string ConvertJobName(JobType job_type)
        {
            var result = "무직";

            switch (job_type)
            {
                case JobType.NONE:
                    result = "무직";
                    break;

                case JobType.GEOIOGIST:
                    result = "지질학";
                    break;

                case JobType.BOTANIST:
                    result = "식물학";
                    break;
            }

            return result;
        }

        public static string GetResearchName(int research_id)
        {
            switch (research_id)
            {
                case 4:
                    return "암석 가공";

                case 5:
                    return "식물 가공";

                case 7:
                    return "텐트 제작";
            }

            return "미정";
        }

        public static List<(int, int, int)> GetRequireResearch(int research_id)
        {
            var result = new List<(int, int, int)>();

            switch (research_id)
            {
                case 4:
                    result.Add((1, 1, 1));
                    break;
                case 5:
                    result.Add((2, 2, 1));
                    break;

                case 7:
                    result.Add((4, 1, 1));
                    result.Add((5, 2, 1));
                    break;
            }

            return result;
        }

        public static List<(int, int)> GetResearchUpgradeCharge(
            JobType job_type,
            ResearchInfo research_info
        )
        {
            var key = "";
            switch (job_type)
            {
                case JobType.GEOIOGIST:
                    key = $"{research_info.research_id}_1_{research_info.geo_level + 1}";
                    break;

                case JobType.BOTANIST:
                    key = $"{research_info.research_id}_2_{research_info.botan_level + 1}";
                    break;

                case JobType.BIOLOGY:
                    key = $"{research_info.research_id}_3_{research_info.bio_level + 1}";
                    break;
            }

            if (!research_upgarde_item_dictionary.TryGetValue(key, out var require_list))
            {
                return new();
            }

            return require_list;
        }

        public static Dictionary<string, List<(int, int)>> research_upgarde_item_dictionary =
            new()
            {
                {
                    "4_1_1",
                    new() { (301000001, 10) }
                },
                {
                    "4_1_2",
                    new() { (301000001, 100) }
                },
                {
                    "5_2_1",
                    new() { (301000002, 10) }
                },
                {
                    "5_2_2",
                    new() { (301000002, 100) }
                },
                {
                    "7_1_1",
                    new() { (301000003, 10), (301000005, 10) }
                },
                {
                    "7_2_1",
                    new() { (301000004, 10), (301000006, 10) }
                },
                {
                    "7_1_2",
                    new() { (301000003, 100), (301000005, 100) }
                },
                {
                    "7_2_2",
                    new() { (301000004, 100), (301000006, 100) }
                },
            };

        public static int GetMakableItemId(
            List<int> research_id_list,
            List<(int, int)> make_materials
        )
        {
            var conditions = new Dictionary<int, List<(int, int)>>();
            foreach (var research_id in research_id_list)
            {
                switch (research_id)
                {
                    case 4:
                        conditions.Add(301000007, new() { (301000001, 3) });
                        conditions.Add(102000003, new() { (102000001, 1), (301000007, 1) });
                        break;

                    case 5:
                        conditions.Add(301000008, new() { (301000002, 3) });
                        conditions.Add(102000004, new() { (102000002, 1), (301000008, 1) });
                        break;

                    case 7:
                        break;
                }
            }

            foreach (var (key, conditionList) in conditions)
            {
                if (make_materials.Count != conditionList.Count)
                {
                    continue;
                }

                bool isMakable = true;
                foreach (var (itemId, count) in conditionList)
                {
                    var material = make_materials.Find(x => x.Item1 == itemId);
                    if (material.Item1 == 0 || material.Item2 != count)
                    {
                        isMakable = false;
                        break;
                    }
                }

                if (isMakable)
                {
                    return key;
                }
            }

            return 0;
        }
    }
}
