namespace FairyGUI;

public class EventListener
{
	private EventBridge _bridge;

	private string _type;

	public string type => _type;

	public bool isEmpty => !_bridge.owner.hasEventListeners(_type);

	public bool isDispatching => _bridge.owner.isDispatching(_type);

	public void Retain()
	{
		_bridge.Retain();
	}

	public void Release()
	{
		_bridge.Release();
	}

	public EventListener(EventDispatcher owner, string type)
	{
		_bridge = owner.GetEventBridge(type);
		_type = type;
	}

	public void AddCapture(EventCallback1 callback)
	{
		_bridge.AddCapture(callback);
	}

	public void RemoveCapture(EventCallback1 callback)
	{
		if (_bridge._isLocking)
		{
			_bridge.Release();
		}
		_bridge.RemoveCapture(callback);
	}

	public void Add(EventCallback1 callback)
	{
		_bridge.Add(callback);
	}

	public void Remove(EventCallback1 callback)
	{
		if (_bridge._isLocking)
		{
			_bridge.Release();
		}
		_bridge.Remove(callback);
	}

	public void Add(EventCallback0 callback)
	{
		_bridge.Add(callback);
	}

	public void Remove(EventCallback0 callback)
	{
		if (_bridge._isLocking)
		{
			_bridge.Release();
		}
		_bridge.Remove(callback);
	}

	public void Set(EventCallback1 callback)
	{
		if (_bridge._isLocking)
		{
			_bridge.Release();
		}
		_bridge.Clear();
		if (callback != null)
		{
			_bridge.Add(callback);
		}
	}

	public void Set(EventCallback0 callback)
	{
		if (_bridge._isLocking)
		{
			_bridge.Release();
		}
		_bridge.Clear();
		if (callback != null)
		{
			_bridge.Add(callback);
		}
	}

	public void Clear()
	{
		_bridge.Clear();
	}

	public bool Call()
	{
		return _bridge.owner.InternalDispatchEvent(_type, _bridge, null, null);
	}

	public bool Call(object data)
	{
		return _bridge.owner.InternalDispatchEvent(_type, _bridge, data, null);
	}

	public bool BubbleCall(object data)
	{
		return _bridge.owner.BubbleEvent(_type, data);
	}

	public bool BubbleCall()
	{
		return _bridge.owner.BubbleEvent(_type, null);
	}

	public bool BroadcastCall(object data)
	{
		return _bridge.owner.BroadcastEvent(_type, data);
	}

	public bool BroadcastCall()
	{
		return _bridge.owner.BroadcastEvent(_type, null);
	}
}
