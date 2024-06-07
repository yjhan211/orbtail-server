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

                case 102000001:
                    result = ("수습 공학자의 헬멧", "제한 없음", " 분해 Lv1 사용 가능", "100", "공학 입문자의 든든한 파트너");
                    break;

                case 102000002:
                    result = ("수습 화학자의 고글", "제한 없음", "정화 Lv1 사용 가능", "100", "화학 입문자의 든든한 파트너");
                    break;

                case 102000003:
                    result = (
                        "공학자의 헬멧",
                        "일반 공학자 이상",
                        "분해 Lv2 사용 가능",
                        "100",
                        "이제 연구자 태가 좀 나는 것 같아요."
                    );
                    break;

                case 102000004:
                    result = (
                        "화학자의 고글",
                        "일반 식물학자 이상",
                        "정화 Lv2 사용 가능",
                        "100",
                        "이제 연구자 태가 좀 나는 것 같아요."
                    );
                    break;

                case 103000001:
                    result = ("수습 연구원의 제복", "제한 없음", "조사 Lv1 사용 가능", "100", "실수는 성장의 밑거름이 될 거에요.");
                    break;

                case 103000002:
                    result = (
                        "연구원의 제복",
                        "일반 연구원 이상",
                        "조사 Lv2 사용 가능",
                        "100",
                        "이제 연구자 태가 좀 나는 것 같아요."
                    );
                    break;

                case 201000001:
                    result = ("밤양갱", "조건 없음", "컨디션 20 회복", "", "우리는 너무 많이 생각하고는 해요.");
                    break;

                case 301000001:
                    result = ("진동 모터", "제작 재료", "", "", "들리지 않아도 알 수 있어요");
                    break;

                case 301000002:
                    result = ("단순 안테나", "제작 재료", "", "", "소통은 단순하고 명료한 게 좋아요.");
                    break;

                case 301000003:
                    result = ("저품질 태양 전지", "제작 재료", "", "", "가늘지만 그만큼 오래 갈 수 있어요.");
                    break;

                case 301000004:
                    result = ("단순 키패드", "제작 재료", "", "", "세상의 모든 것을 0과 1만으로 표현해봐요.");
                    break;

                case 301000005:
                    result = ("저품질 스피커", "제작 재료", "", "", "잡음도 나름의 낭만을 주고는 해요.");
                    break;

                case 301000006:
                    result = ("저품질 가변콘덴서", "제작 재료", "", "", "주파수를 조정할 수 있어요.");
                    break;

                case 301000007:
                    result = ("소형 LCD 화면", "제작 재료", "", "", "작지만 선명한 화면이에요.");
                    break;

                case 301000008:
                    result = ("저용량 메모리", "제작 재료", "", "", "기억할 건 적지만 잊지 않아요");
                    break;

                case 301000009:
                    result = ("저화질 이미지 센서", "제작 재료", "", "", "영상의 질은 떨어져도 순간을 포착할 수 있어요.");
                    break;

                case 301000010:
                    result = ("소형 렌즈", "제작 재료", "", "", "작은 세상을 보여줄 수 있어요");
                    break;

                case 301000011:
                    result = ("저성능 프로세서", "제작 재료", "", "", "느리지만 꾸준히 처리할 수 있어요.");
                    break;

                case 301000012:
                    result = ("소형 배터리", "제작 재료", "", "", "작지만 오래 버틸 수 있어요");
                    break;

                case 301000013:
                    result = ("마그네트론", "제작 재료", "", "", "전자기파의 힘으로 요리를 도와요");
                    break;

                case 301000014:
                    result = ("소형 변압기", "제작 재료", "", "", "다양한 장치에게 도움을 줄 수 있어요");
                    break;

                case 301000015:
                    result = ("저출력 모터", "제작 재료", "", "", "느리지만 꾸준히 돌아갈 수 있어요");
                    break;

                case 301000016:
                    result = ("필터", "제작 재료", "", "", "불순물을 걸러내 깨끗하게 만들어요");
                    break;

                case 301000017:
                    result = ("먹는 샘물", "제작 재료", "", "", "목마름을 해소해주는 깨끗한 물이에요.");
                    break;
                case 301000018:
                    result = ("증류수", "제작 재료", "", "", "불순물이 제거된 순수한 물이에요.");
                    break;
                case 301000019:
                    result = ("황산", "제작 재료", "", "", "강력한 산성 물질이에요. 조심해서 다뤄야 해요.");
                    break;
                case 301000020:
                    result = ("암모니아", "제작 재료", "", "", "자극적인 냄새가 나는 알칼리성 물질이에요.");
                    break;
                case 301000021:
                    result = ("납", "제작 재료", "", "", "무거운 금속이에요. 독성이 있어 주의가 필요해요.");
                    break;
                case 301000022:
                    result = ("카드뮴", "제작 재료", "", "", "은백색의 금속이에요. 독성이 강해요.");
                    break;
                case 301000023:
                    result = ("경유", "제작 재료", "", "", "디젤 엔진에 사용되는 연료에요.");
                    break;
                case 301000024:
                    result = ("등유", "제작 재료", "", "", "램프나 난로에 사용되는 연료에요.");
                    break;
                case 301000025:
                    result = ("그리스", "제작 재료", "", "", "기계의 윤활제로 사용되는 물질이에요.");
                    break;
                case 301000026:
                    result = ("엔진 오일", "제작 재료", "", "", "엔진의 윤활과 냉각을 돕는 오일이에요.");
                    break;
                case 301000027:
                    result = ("아스팔트", "제작 재료", "", "", "도로 포장에 사용되는 검은색 물질이에요.");
                    break;
                case 301000028:
                    result = ("왁스", "제작 재료", "", "", "광택을 내는 데 사용되는 물질이에요.");
                    break;
                case 301000029:
                    result = ("모래", "제작 재료", "", "", "작은 알갱이로 이루어진 퇴적물이에요.");
                    break;
                case 301000030:
                    result = ("점토", "제작 재료", "", "", "물과 혼합하면 점성이 생기는 부드러운 흙이에요.");
                    break;
                case 301000031:
                    result = ("부엽토", "제작 재료", "", "", "낙엽이 분해되어 만들어진 비옥한 흙이에요.");
                    break;
                case 301000032:
                    result = ("비료", "제작 재료", "", "", "작물의 생장을 돕는 영양분이에요.");
                    break;

                case 401000001: // 교체 예정
                    result = ("허름한 텐트", "텐트", "5초마다 컨디션 1 회복", "", "허름하지만 아늑한 느낌이 들어요.");
                    break;
            }

            return result;
        }

        // name, level, job_type, state
        public static (string, int, JobType, PlayerState) GetSkillDetail(int skill_id)
        {
            var result = ("", 0, JobType.NONE, PlayerState.NONE);
            switch (skill_id)
            {
                case 10001:
                    result = (
                        "분해 Lv1: 전자기기로부터 자원을 얻는 기술",
                        1,
                        JobType.ENGINEER,
                        PlayerState.ENGINEER_WORK_1
                    );
                    break;

                case 20001:
                    result = (
                        "정제 Lv1: 환경자원을 정제하는 기술",
                        1,
                        JobType.CHEMIST,
                        PlayerState.CHEMIST_WORK_1
                    );
                    break;

                case 10002:
                    result = (
                        "분해 Lv2: 전자기기로부터 자원을 얻는 기술",
                        2,
                        JobType.ENGINEER,
                        PlayerState.ENGINEER_WORK_1
                    );
                    break;

                case 20002:
                    result = (
                        "정제 Lv2: 환경자원을 정제하는 기술",
                        2,
                        JobType.CHEMIST,
                        PlayerState.CHEMIST_WORK_1
                    );
                    break;

                case 100001:
                    result = ("조사 Lv1: 잠재된 자원을 발견하는 기술", 1, JobType.NONE, PlayerState.EXPLORE_1);
                    break;

                case 100002:
                    result = ("조사 Lv2: 잠재된 자원을 발견하는 기술", 1, JobType.NONE, PlayerState.EXPLORE_1);
                    break;

                case 200001:
                    result = ("제작: 연구 일지를 기반으로 제작하는 기술", 0, JobType.NONE, PlayerState.NONE);
                    break;
            }

            return result;
        }

        public static string GetJobDetail(JobType job_type)
        {
            string result = "";
            switch (job_type)
            {
                case JobType.ENGINEER:
                    result = "전자기기 자원의 활용 방안을 모색하는 연구원";
                    break;

                case JobType.CHEMIST:
                    result = "환경 자원의 정제 방안을 모색하는 연구원 ";
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
                    reward_item_list.Add(301000002);
                    result = ("삐삐", 20, JobType.ENGINEER, 1, "분해 Lv1 스킬이 필요해요.", reward_item_list);
                    break;

                case 10002:
                    reward_item_list.Add(301000003);
                    reward_item_list.Add(301000004);
                    result = (
                        "전자 계산기",
                        20,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 10003:
                    reward_item_list.Add(301000005);
                    reward_item_list.Add(301000006);
                    result = ("라디오", 20, JobType.ENGINEER, 1, "분해 Lv1 스킬이 필요해요.", reward_item_list);
                    break;

                case 10004:
                    reward_item_list.Add(301000007);
                    reward_item_list.Add(301000008);
                    result = (
                        "MP3 플레이어",
                        20,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 10005:
                    reward_item_list.Add(301000009);
                    reward_item_list.Add(301000010);
                    result = (
                        "휴대용 게임기",
                        20,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 10006:
                    reward_item_list.Add(301000011);
                    reward_item_list.Add(301000012);
                    result = (
                        "디지털 카메라",
                        20,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 10007:
                    reward_item_list.Add(301000013);
                    reward_item_list.Add(301000014);
                    result = (
                        "전자레인지",
                        20,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 10008:
                    reward_item_list.Add(301000015);
                    reward_item_list.Add(301000016);
                    result = (
                        "진공청소기",
                        20,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 20001:
                    reward_item_list.Add(301000017);
                    reward_item_list.Add(301000018);
                    result = ("정제수", 20, JobType.CHEMIST, 1, "정제 Lv1 스킬이 필요해요.", reward_item_list);
                    break;

                case 20002:
                    reward_item_list.Add(301000019);
                    reward_item_list.Add(301000020);
                    result = (
                        "산성 폐기물",
                        20,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 20003:
                    reward_item_list.Add(301000021);
                    reward_item_list.Add(301000022);
                    result = (
                        "중금속 잔류물",
                        20,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 20004:
                    reward_item_list.Add(301000023);
                    reward_item_list.Add(301000024);
                    result = (
                        "재생 연료유",
                        20,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 20005:
                    reward_item_list.Add(301000025);
                    reward_item_list.Add(301000026);
                    result = ("윤활유", 20, JobType.CHEMIST, 2, "정제 Lv2 스킬이 필요해요.", reward_item_list);
                    break;

                case 20006:
                    reward_item_list.Add(301000027);
                    reward_item_list.Add(301000028);
                    result = ("타르", 20, JobType.CHEMIST, 2, "정제 Lv2 스킬이 필요해요.", reward_item_list);
                    break;

                case 20007:
                    reward_item_list.Add(301000029);
                    reward_item_list.Add(301000030);
                    result = (
                        "정화된 토양",
                        20,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 20008:
                    reward_item_list.Add(301000031);
                    reward_item_list.Add(301000032);
                    result = (
                        "유기물 분해 토양",
                        20,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;
            }

            return result;
        }

        public static List<int> GetJobResourceUpgradePool(int job_resource_id)
        {
            var result = new List<int>();
            switch (job_resource_id)
            {
                case 100001: // 잔해더미
                    result.Add(10001); // 삐삐
                    result.Add(10002); // 전자 계산기
                    result.Add(10003); // 라디오
                    break;

                case 100002: // 버려진 기계
                    result.Add(10004); // MP3 플레이어
                    result.Add(10005); // 휴대용 게임기
                    result.Add(10006); // 디지털 카메라
                    break;

                case 100003: // 오래된 유물
                    result.Add(10007); // 전자레인지
                    result.Add(10008); // 진공청소기
                    break;

                case 200001: // 산성 폐수
                    result.Add(20001); // 정제수
                    result.Add(20002); // 산성 폐기물
                    result.Add(20003); // 중금속 잔류물
                    break;

                case 200002: // 오래된 기름통
                    result.Add(20004); // 재생 연료유
                    result.Add(20005); // 윤활유
                    result.Add(20006); // 타르
                    break;

                case 200003: // 오염된 토양
                    result.Add(20007); // 정화된 토양
                    result.Add(20008); // 유기물 분해 토양
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
                case PlayerState.ENGINEER_WORK_1:
                    result = ("tool", "tool_1");
                    break;

                case PlayerState.CHEMIST_WORK_1:
                    result = ("tool", "tool_2");
                    break;

                case PlayerState.EXPLORE_1:
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

        public static (JobType, int) GetSkill(int item_id)
        {
            (JobType, int) result = (JobType.NONE, 0);
            switch (item_id)
            {
                case 102000001: // 수습 공학자의 헬멧
                    result = (JobType.ENGINEER, 10001); // 분해 레벨 1
                    break;

                case 102000002: // 수습 화학자의 고글
                    result = (JobType.CHEMIST, 20001); // 정제 레벨 1
                    break;

                case 102000003: // 공학자의 헬멧
                    result = (JobType.ENGINEER, 10002); // 분해 레벨 2
                    break;

                case 102000004: // 화학자의 고글
                    result = (JobType.CHEMIST, 20002); // 정제 레벨 2
                    break;

                case 103000001: // 수습 연구원의 제복
                    result = (JobType.NONE, 100001); // 조사 레벨 1
                    break;

                case 103000002: // 연구원의 제복
                    result = (JobType.NONE, 100002); // 조사 레벨 2
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

        public static bool IsWearableJobInfo(
            int item_id,
            Dictionary<JobType, JobStat> job_stat_dict
        )
        {
            JobType target_job_type = JobType.NONE;
            JobGrade target_job_grade;
            switch (item_id)
            {
                case 102000003:
                    target_job_type = JobType.ENGINEER;
                    target_job_grade = JobGrade.RESEARCHER;
                    break;

                case 102000004:
                    target_job_type = JobType.CHEMIST;
                    target_job_grade = JobGrade.RESEARCHER;
                    break;

                case 103000002:
                    target_job_grade = JobGrade.RESEARCHER;
                    break;

                default:
                    return true;
            }

            if (!job_stat_dict.TryGetValue(target_job_type, out var job_stat))
            {
                return false;
            }

            return target_job_grade <= job_stat.job_grade;
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

                case JobType.ENGINEER:
                    result = "공학자";
                    break;

                case JobType.CHEMIST:
                    result = "화학자";
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
                case JobType.ENGINEER:
                    key = $"{research_info.research_id}_1_{research_info.geo_level + 1}";
                    break;

                case JobType.CHEMIST:
                    key = $"{research_info.research_id}_2_{research_info.botan_level + 1}";
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
                    new() { (301000003, 1) }
                },
                {
                    "7_2_1",
                    new() { (301000004, 1) }
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
                        conditions.Add(301000009, new() { (301000003, 3) });
                        break;

                    case 5:
                        conditions.Add(301000008, new() { (301000002, 3) });
                        conditions.Add(102000004, new() { (102000002, 1), (301000008, 1) });
                        conditions.Add(301000010, new() { (301000006, 3) });
                        break;

                    case 7:
                        conditions.Add(401000001, new() { (301000009, 1), (301000010, 1) });
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

        public static JobType GetJobType(int skill_id)
        {
            var result = JobType.NONE;
            switch (skill_id)
            {
                case 10001:
                case 10002:
                    result = JobType.ENGINEER;
                    break;

                case 20001:
                case 20002:
                    result = JobType.CHEMIST;
                    break;
            }

            return result;
        }

        public static bool IsInstallableItem(int item_id)
        {
            int item_type = (int)(item_id / 1000000);
            int kind = (int)(item_type / 100);

            var installable = kind == 4;
            return installable;
        }
    }
}
