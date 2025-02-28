using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.core;
using network.interfaces;
using user_server.controllers;

namespace user_server.tests.components;

// 테스트용 GameUser 클래스
public class TestGameUser : GameUser

{
    public bool BroadcastUpdateInfoCalled { get; private set; } = false;
    public int BroadcastUpdateInfoCallCount { get; private set; } = 0;
    public bool BroadcastSocialActionCalled { get; private set; } = false;
    public bool BroadcastObjectDestroyCalled { get; private set; }
    public SocialActionType LastSocialActionType { get; private set; }

    public TestGameUser(
        TestUserToken token,
        IRedLockFactory redLock,
        INatsClient natsClient,
        ILogger logger,
        ICacheHelper cacheHelper,
        Action<GameUser> onLeaveCallback,
        ChatController chatController
    ) : base(token, redLock, natsClient, logger, cacheHelper, onLeaveCallback, chatController)
    {
        ResetTrackers();
    }

    public void ResetTrackers()
    {
        BroadcastUpdateInfoCalled = false;
        BroadcastUpdateInfoCallCount = 0;
        BroadcastSocialActionCalled = false;
    }

    public override void BroadcastUpdateInfo<T>(T info)
    {
        BroadcastUpdateInfoCalled = true;
        BroadcastUpdateInfoCallCount++;
        base.BroadcastUpdateInfo(info);
    }
        
    public override void BroadcastSocialAction(PlayerInfo playerInfo, SocialActionType socialActionType)
    {
        BroadcastSocialActionCalled = true;
        LastSocialActionType = socialActionType;
        base.BroadcastSocialAction(playerInfo, socialActionType);
    }
    
    public override void BroadcastObjectDestroy(GameObjectInfo objectInfo)
    {
        BroadcastObjectDestroyCalled = true;
        base.BroadcastObjectDestroy(objectInfo);
    }
}