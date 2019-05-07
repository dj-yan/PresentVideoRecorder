using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PresentVideoRecorder.BusinessLayer
{
    public class WindowCapturer
    {
        private int _fps;

        public WindowCapturer(int fps)
        {
            _fps = fps;
        }

        public void Start()
        { }

        public void Stop()
        { }
    }
}
