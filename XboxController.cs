using System;
using Windows.Gaming.Input;
using StereoKit;

namespace DutchSkies
{

    public class XboxController
    {
        public Gamepad gamepad;
        public GamepadReading previous_reading;
        public GamepadReading current_reading;

        public const GamepadButtons A = GamepadButtons.A;
        public const GamepadButtons B = GamepadButtons.B;
        public const GamepadButtons X = GamepadButtons.X;
        public const GamepadButtons Y = GamepadButtons.Y;

        public const GamepadButtons DPAD_DOWN = GamepadButtons.DPadDown;
        public const GamepadButtons DPAD_UP = GamepadButtons.DPadUp;
        public const GamepadButtons DPAD_LEFT = GamepadButtons.DPadLeft;
        public const GamepadButtons DPAD_RIGHT = GamepadButtons.DPadRight;

        public const GamepadButtons LEFT_SHOULDER = GamepadButtons.LeftShoulder;
        public const GamepadButtons RIGHT_SHOULDER = GamepadButtons.RightShoulder;

        public const GamepadButtons LEFT_THUMBSTICK = GamepadButtons.LeftThumbstick;
        public const GamepadButtons RIGHT_THUMBSTICK = GamepadButtons.RightThumbstick;

        public const GamepadButtons PADDLE_1 = GamepadButtons.Paddle1;
        public const GamepadButtons PADDLE_2 = GamepadButtons.Paddle2;
        public const GamepadButtons PADDLE_3 = GamepadButtons.Paddle3;
        public const GamepadButtons PADDLE_4 = GamepadButtons.Paddle4;

        public const GamepadButtons MENU = GamepadButtons.Menu;
        public const GamepadButtons VIEW = GamepadButtons.View;

        public XboxController()
        {
            Log.Info("Checking for gamepads...");

            // XXX scoped lock needed to access list?
            gamepad = null;

            // Pick first gamepad
            foreach (Gamepad gp in Gamepad.Gamepads)
            {
                Log.Info($"Found gamepad {gp.ToString()}");
                gamepad = gp;
                break;
            }

            Gamepad.GamepadAdded += OnGamepadAdded;
        }

        protected void OnGamepadAdded(object sender, Gamepad args)
        {
            Log.Info($"OnGamepadAdded {sender}, {args.ToString()}");
            // XXX as we don't seem to get OnGamepadRemoved we can't null gamepad and the check is useless
            //if (gamepad == null)
            gamepad = args;
        }

        protected void OnGamepadRemoved(object sender, Gamepad args)
        {
            Log.Info($"OnGamepadRemoved {sender}, {args.ToString()}");
            if (gamepad == args)
                gamepad = null;
        }
        public bool Detected { get { return gamepad != null;  } }

        public bool QueryState()
        {
            // XXX reacquire if not found?
            if (gamepad == null)
                return false;

            previous_reading = current_reading;
            current_reading = gamepad.GetCurrentReading();

            return true;
        }

        // XXX debounce support?
        // All between -1.0 and 1.0
        public double LeftThumbstickX { get { return current_reading.LeftThumbstickX; } }
        public double LeftThumbstickY { get { return current_reading.LeftThumbstickY; } }
        public double RightThumbstickX { get { return current_reading.RightThumbstickX; } }
        public double RightThumbstickY { get { return current_reading.RightThumbstickY; } }

        // All between 0.0 (not depressed) and 1.0 (full depressed)
        public double LeftTrigger { get { return current_reading.LeftTrigger; } }
        public double RightTrigger { get { return current_reading.RightTrigger; } }


        // Returns true on first query after button was pressed (but not yet released)
        public bool Pressed(GamepadButtons button)
        {
            if (gamepad == null)
                return false;

            return ((button & previous_reading.Buttons) == 0 && (button & current_reading.Buttons) != 0);
        }

        // Returns true on first query after button was released (and is not pressed again)
        public bool Released(GamepadButtons button)
        {
            if (gamepad == null)
                return false;

            return ((button & previous_reading.Buttons) != 0 && (button & current_reading.Buttons) == 0);
        }
   }

}