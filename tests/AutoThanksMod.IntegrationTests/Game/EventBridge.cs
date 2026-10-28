using System;

namespace FairyGUI;

public class EventBridge
{
	internal bool _isLocking;

	public EventDispatcher owner;

	private EventCallback0 _callback0;

	private EventCallback1 _callback1;

	private EventCallback1 _captureCallback;

	internal bool _dispatching;

	public bool isEmpty
	{
		get
		{
			if (_callback1 == null && _callback0 == null)
			{
				return _captureCallback == null;
			}
			return false;
		}
	}

	public void Retain()
	{
		_isLocking = true;
	}

	public void Release()
	{
		_isLocking = false;
	}

	public EventBridge(EventDispatcher owner)
	{
		this.owner = owner;
	}

	public void AddCapture(EventCallback1 callback)
	{
		_captureCallback = (EventCallback1)Delegate.Remove(_captureCallback, callback);
		_captureCallback = (EventCallback1)Delegate.Combine(_captureCallback, callback);
	}

	public void RemoveCapture(EventCallback1 callback)
	{
		_captureCallback = (EventCallback1)Delegate.Remove(_captureCallback, callback);
	}

	public void Add(EventCallback1 callback)
	{
		_callback1 = (EventCallback1)Delegate.Remove(_callback1, callback);
		_callback1 = (EventCallback1)Delegate.Combine(_callback1, callback);
	}

	public void Remove(EventCallback1 callback)
	{
		_callback1 = (EventCallback1)Delegate.Remove(_callback1, callback);
	}

	public void Add(EventCallback0 callback)
	{
		_callback0 = (EventCallback0)Delegate.Remove(_callback0, callback);
		_callback0 = (EventCallback0)Delegate.Combine(_callback0, callback);
	}

	public void Remove(EventCallback0 callback)
	{
		_callback0 = (EventCallback0)Delegate.Remove(_callback0, callback);
	}

	public void Clear()
	{
		_callback1 = null;
		_callback0 = null;
		_captureCallback = null;
	}

	public void CallInternal(EventContext context)
	{
		if (_isLocking)
		{
			return;
		}
		_dispatching = true;
		context.sender = owner;
		try
		{
			if (_callback1 != null)
			{
				_callback1(context);
			}
			if (_callback0 != null)
			{
				_callback0();
			}
		}
		finally
		{
			_dispatching = false;
		}
	}

	public void CallCaptureInternal(EventContext context)
	{
		if (_captureCallback == null)
		{
			return;
		}
		_dispatching = true;
		context.sender = owner;
		try
		{
			_captureCallback(context);
		}
		finally
		{
			_dispatching = false;
		}
	}
}
