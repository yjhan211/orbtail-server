using network.common.data.models;
using user_server.controllers;

namespace user_server.tests.components;

public class TestMapObjectController(GameUser user) : MapObjectController(user)
{
    public bool EnqueueUpdateObjectCalled { get; private set; } = false;
    public GameObjectInfo? LastUpdatedObject { get; private set; }

    public override void EnqueueUpdateObject(GameObjectInfo objectInfo)
    {
        EnqueueUpdateObjectCalled = true;
        LastUpdatedObject = objectInfo;
    }
}