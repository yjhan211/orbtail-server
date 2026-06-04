using Xunit;

// GameDataHelper는 static 전역 상태를 Clear+reload하므로, 테스트 클래스 병렬 실행 시
// 서로의 로드 상태를 레이스한다. 직렬 실행으로 고정.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
