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
                case 1001000001:
                    result = ("수습 연구원의 머리", "조건 없음", "열정적인 마음가짐", "100", "초심자의 결의는 언제나 반짝여요.");
                    break;

                case 1001000002:
                    result = ("지질학자의 머리", "조건 없음", "부드러운 인상", "100", "사실 돌을 좋아한다기 보단 돌이 되고 싶었어요.");
                    break;

                case 1002000001:
                    result = (
                        "지질학자의 모자",
                        "수습 이상 지질학자",
                        "채광 스킬 사용 가능",
                        "100",
                        "연구도 좋지만 가장 중요한 건 안전이에요."
                    );
                    break;

                case 2001000001:
                    result = ("밤양갱", "조건 없음", "체력 20 회복", "", "우리는 너무 많이 생각하고는 해요.");
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
            var item_type = (int)(item_id / 1000000);
            return item_type;
        }

        public static (string, string) GetWearItemSpriteInfo(int item_id)
        {
            var item_type = (int)(item_id / 1000000);
            var lavel_number = (int)(item_id % 1000000);

            (string, string) result = ("none", "none");
            switch (item_type)
            {
                case 1001:
                    result = ("hair", $"hair_{lavel_number}");
                    break;

                case 1002:
                    result = ("hat", $"hat_{lavel_number}");
                    break;
            }

            return result;
        }

        public static bool IsWearableItem(int item_id)
        {
            int item_type = (int)(item_id / 1000000);
            int kind = (int)(item_type / 1000);

            var wearable = kind == 1;
            return wearable;
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
            }

            return result;
        }
    }
}
