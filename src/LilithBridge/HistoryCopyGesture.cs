using System;
namespace LilithBridge;

// Returns intent only on a second non-drag release. No clipboard side effects.
internal sealed class HistoryCopyGesture
{
    public const double DoubleClickSeconds = .34;
    public const double DragPixels = 5;
    private DialogueHistoryEntry? _pressed, _previous;
    private double _x, _y, _previousX, _previousY, _downAt, _previousAt;
    private bool _dragged, _second;
    public void Reset() { _pressed = _previous = null; _dragged = _second = false; }
    public static string? Selection(HistoryPageText page,int anchor,int focus,bool explicitCopy)
    {
        if(!explicitCopy)return null;
        int start=Math.Clamp(Math.Min(anchor,focus),0,page.Text.Length),end=Math.Clamp(Math.Max(anchor,focus),0,page.Text.Length);
        return end>start?page.Text.Substring(start,end-start):null;
    }
    public void Down(DialogueHistoryEntry? entry, double x, double y, double now)
    {
        _pressed=entry;_x=x;_y=y;_downAt=now;_dragged=false;
        _second=entry!=null && _previous!=null && entry.EventId==_previous.EventId && entry.SessionId==_previous.SessionId
            && entry.Text==_previous.Text && now-_previousAt>=0 && now-_previousAt<=DoubleClickSeconds
            && Distance(x,y,_previousX,_previousY)<=DragPixels;
    }
    public void Move(double x,double y) { if(_pressed!=null && Distance(x,y,_x,_y)>DragPixels)_dragged=true; }
    public string? Up(DialogueHistoryEntry? entry,double x,double y,double now)
    {
        Move(x,y);
        bool click=!_dragged && _pressed!=null && entry==_pressed && now-_downAt>=0 && now-_downAt<=DoubleClickSeconds;
        string? copy=click && _second ? _pressed!.Text : null;
        _previous=click && !_second ? _pressed : null;_previousAt=now;_previousX=x;_previousY=y;_pressed=null;_second=false;
        return copy;
    }
    private static double Distance(double a,double b,double x,double y)=>Math.Sqrt((a-x)*(a-x)+(b-y)*(b-y));
}
