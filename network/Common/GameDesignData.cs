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
                        "일반 화학자 이상",
                        "정화 Lv2 사용 가능",
                        "100",
                        "이제 연구자 태가 좀 나는 것 같아요."
                    );
                    break;

                case 102000006:
                    result = (
                        "오토바이 헬멧",
                        "일반 공학자 이상",
                        "분해 Lv3 사용 가능",
                        "100",
                        "빠른 속도 못지않게 안전도 중요하죠."
                    );
                    break;

                case 102000007:
                    result = (
                        "오토바이 고글",
                        "일반 식물학자 이상",
                        "정화 Lv3 사용 가능",
                        "100",
                        "빠른 속도 못지않게 안전도 중요하죠."
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

                case 103000004:
                    result = (
                        "습지 탐사 우비",
                        "일반 연구원 이상",
                        "조사 Lv3 사용 가능, 습지 패널티 해제-컨디션",
                        "100",
                        "비바람 불어도 괜찮아요."
                    );
                    break;

                case 104000001:
                    result = ("연구원의 구두", "수습 연구원 이상", "", "100", "편한 착용감과 격식을 모두 갖췄어요.");
                    break;

                case 104000004:
                    result = (
                        "습지 탐사 장화",
                        "일반 연구원 이상",
                        "습지 패널티 해제-이동속도",
                        "100",
                        "진흙 위에서도 당당하게 걸어요."
                    );
                    break;

                case 201000001:
                    result = ("밤양갱", "조건 없음", "컨디션 20 회복", "", "우리는 너무 많이 생각하고는 해요.");
                    break;
                case 301000001:
                    result = ("플라스틱 케이스", "제작 재료", "", "", "가볍고 내구성 있는 보호용 케이스.");
                    break;
                case 301000002:
                    result = ("회로 기판", "제작 재료", "", "", "복잡한 회로가 새겨진 판이에요.");
                    break;
                case 301000003:
                    result = ("고무 키캡", "제작 재료", "", "", "키보드의 손맛을 좋게 해요.");
                    break;
                case 301000004:
                    result = ("유리파편", "제작 재료", "", "", "깨진 유리, 조심히 다뤄요.");
                    break;
                case 301000005:
                    result = ("전자총", "제작 재료", "", "", "전자를 발사하는 장치예요.");
                    break;
                case 301000006:
                    result = ("구리 와이어", "제작 재료", "", "", "전기가 잘 통하는 구리 전선.");
                    break;
                case 301000007:
                    result = ("알루미늄 봉", "제작 재료", "", "", "가볍고 강한 알루미늄 막대.");
                    break;
                case 301000008:
                    result = ("나사", "제작 재료", "", "", "물건을 고정하는 데 써요.");
                    break;
                case 301000009:
                    result = ("철제 패널", "제작 재료", "", "", "단단한 철로 만든 판.");
                    break;
                case 301000010:
                    result = ("금속 손잡이", "제작 재료", "", "", "문이나 서랍을 여는 손잡이.");
                    break;
                case 301000011:
                    result = ("자석 팁", "제작 재료", "", "", "작지만 강한 자성을 가져요.");
                    break;
                case 301000012:
                    result = ("녹슨 드라이버 헤드", "제작 재료", "", "", "사용한 흔적이 있는 드라이버 끝.");
                    break;
                case 301000013:
                    result = ("스프링", "제작 재료", "", "", "탄성 있는 금속 코일.");
                    break;
                case 301000014:
                    result = ("고무 그립", "제작 재료", "", "", "미끄럼 방지용 고무 손잡이.");
                    break;
                case 301000015:
                    result = ("금속 집게", "제작 재료", "", "", "물건을 집는 금속 도구.");
                    break;
                case 301000016:
                    result = ("소형 모터", "제작 재료", "", "", "작지만 강한 회전력의 모터.");
                    break;
                case 301000017:
                    result = ("배터리 팩", "제작 재료", "", "", "휴대용 전원 공급 장치.");
                    break;
                case 301000019:
                    result = ("철조각", "제작 재료", "", "", "다용도로 쓰이는 철 조각.");
                    break;
                case 301000020:
                    result = ("볼트", "제작 재료", "", "", "나사와 쓰이는 고정용 부품.");
                    break;
                case 301000021:
                    result = ("와셔", "제작 재료", "", "", "볼트와 너트 사이의 얇은 원판.");
                    break;
                case 301000022:
                    result = ("나무 조각", "제작 재료", "", "", "다용도로 쓰이는 나무 조각.");
                    break;
                case 301000023:
                    result = ("못", "제작 재료", "", "", "물건을 고정하는 뾰족한 핀.");
                    break;
                case 301000024:
                    result = ("니스 조각", "제작 재료", "", "", "광택을 내는 데 쓰는 조각.");
                    break;
                case 301000025:
                    result = ("금속 레일", "제작 재료", "", "", "물건을 미끄러뜨리는 금속 막대.");
                    break;
                case 301000027:
                    result = ("합판 조각", "제작 재료", "", "", "여러 겹 붙인 튼튼한 판자.");
                    break;
                case 301000028:
                    result = ("금속 턱", "제작 재료", "", "", "물건을 고정하는 금속 부품.");
                    break;
                case 301000030:
                    result = ("회전 베이스", "제작 재료", "", "", "물건을 회전시키는 받침대.");
                    break;
                case 301000031:
                    result = ("톱날", "제작 재료", "", "", "나무나 금속을 자르는 날.");
                    break;
                case 301000032:
                    result = ("목재 손잡이", "제작 재료", "", "", "도구용 나무 손잡이.");
                    break;
                case 301000033:
                    result = ("금속 프레임", "제작 재료", "", "", "구조물의 뼈대가 되는 틀.");
                    break;
                case 301000034:
                    result = ("변압기", "제작 재료", "", "", "전압을 바꾸는 장치.");
                    break;
                case 301000035:
                    result = ("전극 홀더", "제작 재료", "", "", "전극을 고정하는 홀더.");
                    break;
                case 301000036:
                    result = ("케이블", "제작 재료", "", "", "전기나 신호를 전달하는 선.");
                    break;
                case 301000037:
                    result = ("유리판", "제작 재료", "", "", "투명한 평평한 유리.");
                    break;
                case 301000038:
                    result = ("CCD센서", "제작 재료", "", "", "빛을 전기 신호로 바꾸는 센서.");
                    break;
                case 301000039:
                    result = ("스캐닝 모터", "제작 재료", "", "", "스캐너를 움직이는 모터.");
                    break;
                case 301000040:
                    result = ("고무 롤러", "제작 재료", "", "", "종이를 이송하는 고무 바퀴.");
                    break;
                case 301000041:
                    result = ("기어 세트", "제작 재료", "", "", "동력을 전달하는 톱니바퀴들.");
                    break;
                case 301000044:
                    result = ("잉크 잔여물", "제작 재료", "", "", "프린터에 남은 잉크 찌꺼기.");
                    break;
                case 301000045:
                    result = ("스펀지", "제작 재료", "", "", "물을 잘 흡수하는 다공성 물질.");
                    break;
                case 301000046:
                    result = ("금속 외피", "제작 재료", "", "", "전자기기 보호용 금속 케이스.");
                    break;
                case 301000047:
                    result = ("전해질 잔여물", "제작 재료", "", "", "전기 분해 후 남은 물질.");
                    break;
                case 301000048:
                    result = ("탄소봉", "제작 재료", "", "", "전기가 통하는 탄소 막대.");
                    break;
                case 301000049:
                    result = ("구리 접점", "제작 재료", "", "", "전기 회로 연결용 구리 부품.");
                    break;
                case 301000051:
                    result = ("플라스틱 베이스", "제작 재료", "", "", "전자기기용 플라스틱 받침대.");
                    break;
                case 301000053:
                    result = ("다이오드", "제작 재료", "", "", "한 방향으로만 전류가 흐르는 부품.");
                    break;
                case 301000055:
                    result = ("카페인 추출물", "제작 재료", "", "", "각성 효과 있는 카페인 농축액.");
                    break;
                case 301000056:
                    result = ("타우린 파우더", "제작 재료", "", "", "에너지 향상에 좋은 아미노산 가루.");
                    break;
                case 301000057:
                    result = ("비타민 복합체", "제작 재료", "", "", "다양한 비타민 혼합 영양제.");
                    break;
                case 301000058:
                    result = ("참치 통조림", "제작 재료", "", "", "오래 보관 가능한 참치 캔.");
                    break;
                case 301000059:
                    result = ("콩 통조림", "제작 재료", "", "", "영양 높은 콩 캔.");
                    break;
                case 301000060:
                    result = ("과일 통조림", "제작 재료", "", "", "오래 보관 가능한 과일 캔.");
                    break;
                case 301000061:
                    result = ("인스턴스 면", "제작 재료", "", "", "빠르게 조리되는 즉석 면.");
                    break;
                case 301000062:
                    result = ("건조 야채", "제작 재료", "", "", "수분 제거된 오래가는 야채.");
                    break;
                case 301000063:
                    result = ("조미료 팩", "제작 재료", "", "", "다양한 맛을 내는 조미료 모음.");
                    break;
                case 301000064:
                    result = ("정제 소금", "제작 재료", "", "", "불순물 없는 순수한 소금.");
                    break;
                case 301000065:
                    result = ("결정화 설탕", "제작 재료", "", "", "순수 설탕의 결정체.");
                    break;
                case 301000066:
                    result = ("후추 가루", "제작 재료", "", "", "음식에 풍미를 더하는 향신료.");
                    break;
                case 301000067:
                    result = ("압축 단백질", "제작 재료", "", "", "고농축 단백질 보충제.");
                    break;
                case 301000068:
                    result = ("견과류 조각", "제작 재료", "", "", "영양 높은 견과류 모음.");
                    break;
                case 301000069:
                    result = ("건조 과일 조각", "제작 재료", "", "", "수분 제거된 오래가는 과일.");
                    break;
                case 301000070:
                    result = ("초콜릿 조각", "제작 재료", "", "", "달콤한 에너지 보충용 간식.");
                    break;
                case 301000071:
                    result = ("카카오 버터", "제작 재료", "", "", "초콜릿의 주 원료, 카카오 기름.");
                    break;
                case 301000072:
                    result = ("김치", "제작 재료", "", "", "영양 높은 한국 전통 발효 식품.");
                    break;
                case 301000073:
                    result = ("피클", "제작 재료", "", "", "식초에 절인 상큼한 채소.");
                    break;
                case 301000074:
                    result = ("딸기 잼", "제작 재료", "", "", "달콤한 딸기로 만든 잼.");
                    break;
                case 301000075:
                    result = ("포도 젤리", "제작 재료", "", "", "포도 맛 나는 간식용 젤리.");
                    break;
                case 301000076:
                    result = ("오렌지 마말레이드", "제작 재료", "", "", "오렌지 껍질 들어간 특별한 잼.");
                    break;
                case 301000077:
                    result = ("소고기 육포", "제작 재료", "", "", "오래 보관 가능한 말린 소고기.");
                    break;
                case 301000078:
                    result = ("돼지고기 육포", "제작 재료", "", "", "오래 보관 가능한 말린 돼지고기.");
                    break;
                case 301000079:
                    result = ("백금 와이어", "제작 재료", "", "", "고순도 백금으로 만든 가는 선.");
                    break;
                case 301000080:
                    result = ("유리튜브", "제작 재료", "", "", "실험용 투명한 유리관.");
                    break;
                case 301000082:
                    result = ("유리 전극", "제작 재료", "", "", "전기화학 실험용 유리 전극.");
                    break;
                case 301000084:
                    result = ("디스플레이 패널", "제작 재료", "", "", "영상 표시용 평면 장치.");
                    break;
                case 301000085:
                    result = ("인산염 결정", "제작 재료", "", "", "인과 산소로 된 결정체.");
                    break;
                case 301000086:
                    result = ("염화물 용액", "제작 재료", "", "", "염화물이 녹아있는 액체.");
                    break;
                case 301000087:
                    result = ("시약병", "제작 재료", "", "", "화학 실험용 작은 유리병.");
                    break;
                case 301000088:
                    result = ("구리 조각", "제작 재료", "", "", "전기가 잘 통하는 구리 조각.");
                    break;
                case 301000089:
                    result = ("부식 생성물", "제작 재료", "", "", "금속이 녹슨 후 생긴 물질.");
                    break;
                case 301000090:
                    result = ("열전도 코팅", "제작 재료", "", "", "열을 잘 전달하는 특수 코팅.");
                    break;
                case 301000091:
                    result = ("열전대 와이어", "제작 재료", "", "", "온도 측정용 특수 전선.");
                    break;
                case 301000092:
                    result = ("세라믹 보호관", "제작 재료", "", "", "고온에 강한 세라믹 관.");
                    break;
                case 301000093:
                    result = ("신호 증폭기", "제작 재료", "", "", "약한 신호를 강하게 만드는 장치.");
                    break;
                case 301000094:
                    result = ("임펠러", "제작 재료", "", "", "유체를 움직이는 회전 날개.");
                    break;
                case 301000095:
                    result = ("고무 호스", "제작 재료", "", "", "유연한 고무로 만든 관.");
                    break;
                case 301000096:
                    result = ("실링 개스킷", "제작 재료", "", "", "틈새를 막는 밀봉재.");
                    break;
                case 301000097:
                    result = ("활성탄", "제작 재료", "", "", "불순물을 잡아내는 다공성 물질.");
                    break;
                case 301000098:
                    result = ("멤브레인 시트", "제작 재료", "", "", "특정 물질만 통과시키는 막.");
                    break;
                case 301000099:
                    result = ("폴리프로필렌 케이스", "제작 재료", "", "", "내구성 좋은 플라스틱 케이스.");
                    break;
                case 301000100:
                    result = ("실리카 겔", "제작 재료", "", "", "습기를 빨아들이는 건조제.");
                    break;
                case 301000101:
                    result = ("이온 교환 수지", "제작 재료", "", "", "물속 이온을 제거하는 물질.");
                    break;
                case 301000102:
                    result = ("스테인리스 스틸 용기", "제작 재료", "", "", "녹슬지 않는 강철 용기.");
                    break;
                case 302000001:
                    result = ("강화 플라스틱", "제작 재료", "", "", "내구성이 향상된 플라스틱 소재.");
                    break;
                case 302000002:
                    result = ("유연성 강화 키트", "제작 재료", "", "", "재료의 유연성을 높이는 키트.");
                    break;
                case 302000003:
                    result = ("전도성 섬유", "제작 재료", "", "", "전기를 전도하는 특수 섬유.");
                    break;
                case 302000004:
                    result = ("내화 섬유", "제작 재료", "", "", "열에 강한 특수 섬유.");
                    break;
                case 302000005:
                    result = ("충격 흡수 패드", "제작 재료", "", "", "충격을 효과적으로 흡수하는 패드.");
                    break;
                case 302000006:
                    result = ("방탄 섬유", "제작 재료", "", "", "부상을 막을 수 있는 고강도 섬유.");
                    break;
                case 401000001:
                    result = ("허름한 텐트", "텐트", "5초마다 컨디션 1 회복", "", "허름해도 아늑한 느낌이 들어요.");
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
                    result = ("분해", 1, JobType.ENGINEER, PlayerState.ENGINEER_WORK_1);
                    break;

                case 20001:
                    result = ("정제", 1, JobType.CHEMIST, PlayerState.CHEMIST_WORK_1);
                    break;

                case 10002:
                    result = ("분해", 2, JobType.ENGINEER, PlayerState.ENGINEER_WORK_1);
                    break;

                case 20002:
                    result = ("정제", 2, JobType.CHEMIST, PlayerState.CHEMIST_WORK_1);
                    break;

                case 30001:
                    result = ("분해", 3, JobType.ENGINEER, PlayerState.ENGINEER_WORK_1);
                    break;

                case 30002:
                    result = ("정제", 3, JobType.CHEMIST, PlayerState.CHEMIST_WORK_1);
                    break;

                case 100001:
                    result = ("조사", 1, JobType.NONE, PlayerState.EXPLORE_1);
                    break;

                case 100002:
                    result = ("조사", 2, JobType.NONE, PlayerState.EXPLORE_1);
                    break;

                case 100003:
                    result = ("조사", 3, JobType.NONE, PlayerState.EXPLORE_1);
                    break;

                case 200001:
                    result = ("제작", 0, JobType.NONE, PlayerState.NONE);
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
                    result = "공학자";
                    break;

                case JobType.CHEMIST:
                    result = "화학자";
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

        public static (string, int, JobType, int, string, List<int>) GetExploreTargetDetail(
            int explore_target_id
        )
        {
            var reward_item_list = new List<int>();
            var result = ("", 0, JobType.NONE, 0, "", reward_item_list);
            switch (explore_target_id)
            {
                case 100001:
                    reward_item_list.Add(10001);
                    reward_item_list.Add(10002);
                    reward_item_list.Add(10003);
                    result = (
                        "뒤집힌 사무용 책상",
                        20,
                        JobType.ENGINEER,
                        1,
                        "조사 Lv1 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100002:
                    reward_item_list.Add(10004);
                    reward_item_list.Add(10005);
                    reward_item_list.Add(10006);
                    result = (
                        "녹슨 공구함",
                        20,
                        JobType.ENGINEER,
                        1,
                        "조사 Lv1 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100003:
                    reward_item_list.Add(10007);
                    reward_item_list.Add(10008);
                    reward_item_list.Add(10009);
                    result = (
                        "부서진 선반",
                        20,
                        JobType.ENGINEER,
                        1,
                        "조사 Lv1 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100004:
                    reward_item_list.Add(10010);
                    reward_item_list.Add(10011);
                    reward_item_list.Add(10012);
                    result = (
                        "먼지 쌓인 작업대",
                        20,
                        JobType.ENGINEER,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100005:
                    reward_item_list.Add(10013);
                    reward_item_list.Add(10014);
                    reward_item_list.Add(10015);
                    result = (
                        "고장난 복사기",
                        20,
                        JobType.ENGINEER,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100006:
                    reward_item_list.Add(10016);
                    reward_item_list.Add(10017);
                    reward_item_list.Add(10018);
                    result = (
                        "부식된 배터리 보관함",
                        20,
                        JobType.ENGINEER,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100007:
                    reward_item_list.Add(10019);
                    reward_item_list.Add(10020);
                    reward_item_list.Add(10021);
                    result = (
                        "침수 센서",
                        20,
                        JobType.ENGINEER,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100008:
                    reward_item_list.Add(10022);
                    reward_item_list.Add(10023);
                    reward_item_list.Add(10024);
                    result = (
                        "부유 태양판",
                        20,
                        JobType.ENGINEER,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100009:
                    reward_item_list.Add(10025);
                    reward_item_list.Add(10026);
                    reward_item_list.Add(10027);
                    result = (
                        "무선 부표",
                        20,
                        JobType.ENGINEER,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100010:
                    reward_item_list.Add(10028);
                    reward_item_list.Add(10029);
                    reward_item_list.Add(10030);
                    result = (
                        "습지 드론",
                        20,
                        JobType.ENGINEER,
                        3,
                        "조사 Lv3 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100011:
                    reward_item_list.Add(10031);
                    reward_item_list.Add(10032);
                    reward_item_list.Add(10033);
                    result = (
                        "지반 스캐너",
                        20,
                        JobType.ENGINEER,
                        3,
                        "조사 Lv3 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 100012:
                    reward_item_list.Add(10034);
                    reward_item_list.Add(10035);
                    reward_item_list.Add(10036);
                    result = (
                        "안개 포집망",
                        20,
                        JobType.ENGINEER,
                        3,
                        "조사 Lv3 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200001:
                    reward_item_list.Add(20001);
                    reward_item_list.Add(20002);
                    reward_item_list.Add(20003);
                    result = (
                        "파손된 자판기",
                        20,
                        JobType.CHEMIST,
                        1,
                        "조사 Lv1 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200002:
                    reward_item_list.Add(20004);
                    reward_item_list.Add(20005);
                    reward_item_list.Add(20006);
                    result = (
                        "오래된 캐비닛",
                        20,
                        JobType.CHEMIST,
                        1,
                        "조사 Lv1 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200003:
                    reward_item_list.Add(20007);
                    reward_item_list.Add(20008);
                    reward_item_list.Add(20009);
                    result = (
                        "녹슨 냉장고",
                        20,
                        JobType.CHEMIST,
                        1,
                        "조사 Lv1 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200004:
                    reward_item_list.Add(20010);
                    reward_item_list.Add(20011);
                    reward_item_list.Add(20012);
                    result = (
                        "고장난 pH조절 탱크",
                        20,
                        JobType.CHEMIST,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200005:
                    reward_item_list.Add(20013);
                    reward_item_list.Add(20014);
                    reward_item_list.Add(20015);
                    result = (
                        "녹슨 열교환기",
                        20,
                        JobType.CHEMIST,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200006:
                    reward_item_list.Add(20016);
                    reward_item_list.Add(20017);
                    reward_item_list.Add(20018);
                    result = (
                        "오염된 여과 시스템",
                        20,
                        JobType.CHEMIST,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200007:
                    reward_item_list.Add(20019);
                    reward_item_list.Add(20020);
                    reward_item_list.Add(20021);
                    result = (
                        "이끼 덩어리",
                        20,
                        JobType.CHEMIST,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200008:
                    reward_item_list.Add(20022);
                    reward_item_list.Add(20023);
                    reward_item_list.Add(20024);
                    result = (
                        "진흙 웅덩이",
                        20,
                        JobType.CHEMIST,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200009:
                    reward_item_list.Add(20025);
                    reward_item_list.Add(20026);
                    reward_item_list.Add(20027);
                    result = (
                        "부식된 배터리",
                        20,
                        JobType.CHEMIST,
                        2,
                        "조사 Lv2 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200010:
                    reward_item_list.Add(20028);
                    reward_item_list.Add(20029);
                    reward_item_list.Add(20030);
                    result = (
                        "빗물 집수기",
                        20,
                        JobType.CHEMIST,
                        3,
                        "조사 Lv3 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200011:
                    reward_item_list.Add(20031);
                    reward_item_list.Add(20032);
                    reward_item_list.Add(20033);
                    result = (
                        "부유 식물",
                        20,
                        JobType.CHEMIST,
                        3,
                        "조사 Lv3 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;

                case 200012:
                    reward_item_list.Add(20034);
                    reward_item_list.Add(20035);
                    reward_item_list.Add(20036);
                    result = (
                        "가스 차단기",
                        20,
                        JobType.CHEMIST,
                        3,
                        "조사 Lv3 스킬이 필요해요.",
                        reward_item_list
                    );
                    break;
            }

            return result;
        }

        public static (string, int, JobType, int, string, List<int>) GetJobResourceDetail(
            int job_resource_id
        )
        {
            var jobResources = new Dictionary<int, (string, int, JobType, int, string, List<int>)>
            {
                {
                    10001,
                    (
                        "손상된 키보드",
                        100001,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        new List<int> { 301000001, 301000002, 301000003 }
                    )
                },
                {
                    10002,
                    (
                        "깨진 모니터",
                        100001,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        new List<int> { 301000004, 301000005, 301000006 }
                    )
                },
                {
                    10003,
                    (
                        "구부러진 금속 프레임",
                        100001,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        new List<int> { 301000007, 301000008, 301000009 }
                    )
                },
                {
                    10004,
                    (
                        "녹슨 드라이버 세트",
                        100002,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        new List<int> { 301000010, 301000011, 301000012 }
                    )
                },
                {
                    10005,
                    (
                        "부식된 플라이어",
                        100002,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        new List<int> { 301000013, 301000014, 301000015 }
                    )
                },
                {
                    10006,
                    (
                        "망가진 전동 드릴",
                        100002,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        new List<int> { 301000016, 301000017, 301000019 }
                    )
                },
                {
                    10007,
                    (
                        "휘어진 금속 브래킷",
                        100003,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        new List<int> { 301000019, 301000020, 301000021 }
                    )
                },
                {
                    10008,
                    (
                        "깨진 나무 판자",
                        100003,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        new List<int> { 301000022, 301000023, 301000024 }
                    )
                },
                {
                    10009,
                    (
                        "찌그러진 서랍",
                        100003,
                        JobType.ENGINEER,
                        1,
                        "분해 Lv1 스킬이 필요해요.",
                        new List<int> { 301000025, 301000027, 301000028 }
                    )
                },
                {
                    10010,
                    (
                        "오래된 바이스",
                        100004,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        new List<int> { 301000028, 301000029, 301000030 }
                    )
                },
                {
                    10011,
                    (
                        "녹슨 줄톱",
                        100004,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        new List<int> { 301000031, 301000032, 301000033 }
                    )
                },
                {
                    10012,
                    (
                        "부서진 용접기",
                        100004,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        new List<int> { 301000034, 301000035, 301000036 }
                    )
                },
                {
                    10013,
                    (
                        "파손된 스캐너",
                        100005,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        new List<int> { 301000037, 301000038, 301000039 }
                    )
                },
                {
                    10014,
                    (
                        "고장난 급지 장치",
                        100005,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        new List<int> { 301000040, 301000041, 301000042 }
                    )
                },
                {
                    10015,
                    (
                        "잉크 카트리지",
                        100005,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        new List<int> { 301000043, 301000044, 301000045 }
                    )
                },
                {
                    10016,
                    (
                        "누액된 배터리",
                        100006,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        new List<int> { 301000046, 301000047, 301000048 }
                    )
                },
                {
                    10017,
                    (
                        "부식된 단자",
                        100006,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        new List<int> { 301000049, 301000050, 301000051 }
                    )
                },
                {
                    10018,
                    (
                        "깨진 충전기",
                        100006,
                        JobType.ENGINEER,
                        2,
                        "분해 Lv2 스킬이 필요해요.",
                        new List<int> { 301000052, 301000053, 301000054 }
                    )
                },
                {
                    20001,
                    (
                        "에너지 드링크",
                        200001,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        new List<int> { 301000055, 301000056, 301000057 }
                    )
                },
                {
                    20002,
                    (
                        "단백질 바",
                        200001,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        new List<int> { 301000067, 301000068, 301000069 }
                    )
                },
                {
                    20003,
                    (
                        "초콜릿 바",
                        200001,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        new List<int> { 301000070, 301000071 }
                    )
                },
                {
                    20004,
                    (
                        "통조림",
                        200002,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        new List<int> { 301000058, 301000059, 301000060 }
                    )
                },
                {
                    20005,
                    (
                        "건조식품",
                        200002,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        new List<int> { 301000061, 301000062, 301000063 }
                    )
                },
                {
                    20006,
                    (
                        "조미료",
                        200002,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        new List<int> { 301000064, 301000065, 301000066 }
                    )
                },
                {
                    20007,
                    (
                        "발효된 채소",
                        200003,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        new List<int> { 301000072, 301000073 }
                    )
                },
                {
                    20008,
                    (
                        "오래된 잼",
                        200003,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        new List<int> { 301000074, 301000075, 301000076 }
                    )
                },
                {
                    20009,
                    (
                        "건조된 육포",
                        200003,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        new List<int> { 301000077, 301000078 }
                    )
                },
                {
                    20010,
                    (
                        "부식된 전극",
                        200004,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        new List<int> { 301000079, 301000080, 301000081 }
                    )
                },
                {
                    20011,
                    (
                        "깨진 pH미터",
                        200004,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        new List<int> { 301000082, 301000083, 301000084 }
                    )
                },
                {
                    20012,
                    (
                        "누출된 완충용액",
                        200004,
                        JobType.CHEMIST,
                        1,
                        "정제 Lv1 스킬이 필요해요.",
                        new List<int> { 301000085, 301000086, 301000087 }
                    )
                },
                {
                    20013,
                    (
                        "구멍 난 열교환 파이프",
                        200005,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        new List<int> { 301000088, 301000089, 301000090 }
                    )
                },
                {
                    20014,
                    (
                        "망가진 온도 센서",
                        200005,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        new List<int> { 301000091, 301000092, 301000093 }
                    )
                },
                {
                    20015,
                    (
                        "파손된 펌프",
                        200005,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        new List<int> { 301000094, 301000095, 301000096 }
                    )
                },
                {
                    20016,
                    (
                        "막힌 필터 카트리지",
                        200006,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        new List<int> { 301000097, 301000098, 301000099 }
                    )
                },
                {
                    20017,
                    (
                        "오염된 여과조",
                        200006,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        new List<int> { 301000100, 301000101, 301000102 }
                    )
                },
                {
                    20018,
                    (
                        "고장난 압력 게이지",
                        200006,
                        JobType.CHEMIST,
                        2,
                        "정제 Lv2 스킬이 필요해요.",
                        new List<int> { 301000103, 301000104, 301000105 }
                    )
                }
            };

            if (jobResources.TryGetValue(job_resource_id, out var result))
            {
                return result;
            }

            return ("", 0, JobType.NONE, 0, "", new List<int>());
        }

        public static List<int> GetExploreResultPool(int job_resource_id)
        {
            var result = new List<int>();
            switch (job_resource_id)
            {
                case 100001:
                    result.AddRange(new[] { 10001, 10002, 10003 });
                    break;
                case 100002:
                    result.AddRange(new[] { 10004, 10005, 10006 });
                    break;
                case 100003:
                    result.AddRange(new[] { 10007, 10008, 10009 });
                    break;
                case 100004:
                    result.AddRange(new[] { 10010, 10011, 10012 });
                    break;
                case 100005:
                    result.AddRange(new[] { 10013, 10014, 10015 });
                    break;
                case 100006:
                    result.AddRange(new[] { 10016, 10017, 10018 });
                    break;
                case 100007:
                    result.AddRange(new[] { 10019, 10020, 10021 });
                    break;
                case 100008:
                    result.AddRange(new[] { 10022, 10023, 10024 });
                    break;
                case 100009:
                    result.AddRange(new[] { 10025, 10026, 10027 });
                    break;
                case 100010:
                    result.AddRange(new[] { 10028, 10029, 10030 });
                    break;
                case 100011:
                    result.AddRange(new[] { 10031, 10032, 10033 });
                    break;
                case 100012:
                    result.AddRange(new[] { 10034, 10035, 10036 });
                    break;
                case 200001:
                    result.AddRange(new[] { 20001, 20002, 20003 });
                    break;
                case 200002:
                    result.AddRange(new[] { 20004, 20005, 20006 });
                    break;
                case 200003:
                    result.AddRange(new[] { 20007, 20008, 20009 });
                    break;
                case 200004:
                    result.AddRange(new[] { 20010, 20011, 20012 });
                    break;
                case 200005:
                    result.AddRange(new[] { 20013, 20014, 20015 });
                    break;
                case 200006:
                    result.AddRange(new[] { 20016, 20017, 20018 });
                    break;
                case 200007:
                    result.AddRange(new[] { 20019, 20020, 20021 });
                    break;
                case 200008:
                    result.AddRange(new[] { 20022, 20023, 20024 });
                    break;
                case 200009:
                    result.AddRange(new[] { 20025, 20026, 20027 });
                    break;
                case 200010:
                    result.AddRange(new[] { 20028, 20029, 20030 });
                    break;
                case 200011:
                    result.AddRange(new[] { 20031, 20032, 20033 });
                    break;
                case 200012:
                    result.AddRange(new[] { 20034, 20035, 20036 });
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

        public static (JobType, int, int) GetSkill(int item_id)
        {
            (JobType, int, int) result = (JobType.NONE, 0, 0);
            switch (item_id)
            {
                case 102000001: // 수습 공학자의 헬멧
                    result = (JobType.ENGINEER, 10001, 1); // 분해 레벨 1
                    break;

                case 102000002: // 수습 화학자의 고글
                    result = (JobType.CHEMIST, 20001, 1); // 정제 레벨 1
                    break;

                case 102000003: // 공학자의 헬멧
                    result = (JobType.ENGINEER, 10002, 2); // 분해 레벨 2
                    break;

                case 102000004: // 화학자의 고글
                    result = (JobType.CHEMIST, 20002, 2); // 정제 레벨 2
                    break;

                case 102000006: // 오토바이 헬멧
                    result = (JobType.ENGINEER, 10003, 3); // 분해 레벨 3
                    break;

                case 102000007: // 오토바이 고글
                    result = (JobType.CHEMIST, 20003, 3); // 정제 레벨 3
                    break;

                case 103000001: // 수습 연구원의 제복
                    result = (JobType.NONE, 100001, 1); // 조사 레벨 1
                    break;

                case 103000002: // 연구원의 제복
                    result = (JobType.NONE, 100002, 2); // 조사 레벨 2
                    break;

                case 103000004: // 습지 탐사 우비
                    result = (JobType.NONE, 100003, 3); // 조사 레벨 3
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
            JobGrade target_job_grade = JobGrade.NONE;

            switch (item_id)
            {
                case 102000003:
                case 102000006:
                    target_job_type = JobType.ENGINEER;
                    target_job_grade = JobGrade.RESEARCHER;
                    break;

                case 102000004:
                case 102000007:
                    target_job_type = JobType.CHEMIST;
                    target_job_grade = JobGrade.RESEARCHER;
                    break;

                case 103000002:
                case 103000004:
                case 104000004:
                    target_job_grade = JobGrade.RESEARCHER;
                    break;

                default:
                    return true;
            }

            if (target_job_type == JobType.NONE)
            {
                return job_stat_dict.Any(kvp => kvp.Value.job_grade >= target_job_grade);
            }
            else
            {
                if (!job_stat_dict.TryGetValue(target_job_type, out var job_stat))
                {
                    return false;
                }
                return job_stat.job_grade >= target_job_grade;
            }
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
                    result = "수습";
                    break;

                case JobGrade.RESEARCHER:
                    result = "일반";
                    break;

                case JobGrade.ASSOCIATE:
                    result = "주임";
                    break;

                case JobGrade.SENIOR_ASSOCIATE:
                    result = "선임";
                    break;

                case JobGrade.PRINCIPAL:
                    result = "책임";
                    break;

                case JobGrade.LEAD:
                    result = "수석";
                    break;

                case JobGrade.CHIEF:
                    result = "대가";
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

        public static List<ResearchInfo> GetRequireResearch(int research_id)
        {
            var result = new List<ResearchInfo>();
            switch (research_id)
            {
                case 1: // 재료 공학
                    break;

                case 2: // 식량 제작
                    result.Add(new(1, 1)); // 재료 공학 1레벨
                    result.Add(new(3, 1)); // 분석 화학 1레벨
                    break;

                case 3: // 분석 화학
                    break;

                case 4: // 소재 공학
                    result.Add(new(1, 2)); // 재료 공학 2레벨
                    break;

                case 5: // 장비 제작
                    result.Add(new(4, 1)); // 소재 공학 1레벨
                    result.Add(new(6, 1)); // 유기 화학 1레벨
                    break;

                case 6: // 유기 화학
                    result.Add(new(3, 2)); // 분석 화학 2레벨
                    break;

                case 7: // 제어 공학
                    result.Add(new(1, 5)); // 재료 공학 5레벨
                    result.Add(new(4, 5)); // 소재 공학 5레벨
                    break;

                case 8: // 에너지 제작
                    result.Add(new(7, 1)); // 제어 공학 1레벨
                    result.Add(new(9, 1)); // 무기 화학 1레벨
                    break;

                case 9: // 무기 화학
                    result.Add(new(3, 5)); // 분석 화학 5레벨
                    result.Add(new(6, 5)); // 유기 화학 5레벨
                    break;

                case 10: // 전자 공학
                    result.Add(new(1, 8)); // 재료 공학 8레벨
                    result.Add(new(4, 8)); // 소재 공학 8레벨
                    result.Add(new(7, 8)); // 제어 공학 8레벨
                    break;

                case 11: // 의약품 제작
                    result.Add(new(10, 1)); // 전자 공학 1레벨
                    result.Add(new(12, 1)); // 생화학 1레벨
                    break;

                case 12: // 생화학
                    result.Add(new(3, 8)); // 분석 화학 8레벨
                    result.Add(new(6, 8)); // 유기 화학 8레벨
                    result.Add(new(9, 8)); // 무기 화학 8레벨
                    break;
            }

            return result;
        }

        public static int GetMakableItemId(
            List<(int id, int level)> researches,
            List<(int id, int count)> make_materials
        )
        {
            var conditions = new Dictionary<int, List<(int id, int count)>>();

            // 재료 공학 레벨 1
            if (researches.Any(r => r.id == 1 && r.level >= 1))
            {
                conditions.Add(302000001, new() { (301000001, 1), (301000008, 1) }); // 강화 플라스틱 (플라스틱 케이스 + 나사)
                conditions.Add(302000002, new() { (301000003, 1), (301000013, 1) }); // 유연성 강화 키트 (고무 키캡 + 스프링)
            }

            // 재료 공학 레벨 2
            if (researches.Any(r => r.id == 1 && r.level >= 2))
            {
                conditions.Add(302000003, new() { (301000006, 1), (301000014, 1) }); // 전도성 섬유 (구리 와이어 + 고무 그립)
                conditions.Add(302000004, new() { (301000027, 1), (301000024, 1) }); // 내화 섬유 (합판 조각 + 니스 조각)
            }

            // 소재 공학 레벨 1
            if (researches.Any(r => r.id == 4 && r.level >= 1))
            {
                conditions.Add(302000005, new() { (302000001, 1), (302000003, 1) }); // 충격 흡수 패드 (강화 플라스틱 + 전도성 섬유)
                conditions.Add(302000006, new() { (302000002, 1), (302000004, 1) }); // 방탄 섬유 (유연성 강화 키트 + 내화 섬유)
            }

            // 장비 제작 레벨 1
            if (researches.Any(r => r.id == 5 && r.level >= 1))
            {
                conditions.Add(102000003, new() { (102000001, 1), (302000005, 1) }); // 정식 공학자의 헬멧 (수습 공학자의 헬멧 + 충격 흡수 패드)
                conditions.Add(102000004, new() { (102000002, 1), (302000005, 1) }); // 정식 화학자의 고글 (수습 화학자의 고글 + 충격 흡수 패드)
                conditions.Add(103000002, new() { (103000001, 1), (302000006, 1) }); // 정식 연구원의 제복 (수습 연구원의 제복 + 방탄 섬유)
            }

            // 장비 제작 레벨 2
            if (researches.Any(r => r.id == 5 && r.level >= 2))
            {
                conditions.Add(102000006, new() { (102000003, 1), (302000005, 1), (302000006, 1) }); // 오토바이 헬멧 (정식 공학자의 헬멧 + 충격 흡수 패드 + 방탄 섬유)
                conditions.Add(102000007, new() { (102000004, 1), (302000005, 1), (302000006, 1) }); // 오토바이 고글 (정식 공학자의 고글 + 충격 흡수 패드 + 방탄 섬유)
                conditions.Add(103000004, new() { (103000002, 1), (302000005, 1), (302000006, 1) }); // 습지 탐사 우비 (정식 연구원의 제복 + 충격 흡수 패드 + 방탄 섬유)
                conditions.Add(104000002, new() { (104000004, 1), (302000005, 1), (302000006, 1) }); // 습지 탐사 장화 (연구원의 구두 + 충격 흡수 패드 + 방탄 섬유)
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
                    var material = make_materials.Find(x => x.id == itemId);
                    if (material.id == 0 || material.count < count)
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
