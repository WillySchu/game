using System;
using UnityEngine;

namespace RaidZero.World
{
    /// <summary>
    /// The player's settings, kept on this machine (PlayerPrefs) and changed in the escape menu's Settings.
    /// Changes apply straight away; <see cref="Save"/> writes them to disk (the menu calls it when you leave
    /// Settings, and Unity saves on quitting anyway).
    ///
    /// The look settings are applied on the client before the look input is sent (<see cref="LookDegrees"/>),
    /// so the server turns the player exactly as far as the client did, whatever each player has chosen.
    /// </summary>
    public static class GameSettings
    {
        public const float DefaultMouseSensitivity = 1f;
        public const float MinMouseSensitivity = 0.1f;
        public const float MaxMouseSensitivity = 3f;

        /// <summary>How many degrees the view turns per unit of mouse movement at sensitivity 1.</summary>
        public const float LookDegreesPerUnit = 0.5f;

        private const string MouseSensitivityKey = "settings.mouse_sensitivity";
        private const string InvertLookKey = "settings.invert_look";

        private static bool _loaded;
        private static float _mouseSensitivity = DefaultMouseSensitivity;
        private static bool _invertLook;

        /// <summary>Raised when a setting changes.</summary>
        public static event Action Changed;

        /// <summary>A multiplier on how far the mouse turns the view: 1 is normal, 0.5 half as far.</summary>
        public static float MouseSensitivity
        {
            get
            {
                Load();
                return _mouseSensitivity;
            }

            set
            {
                Load();
                var clamped = Mathf.Clamp(value, MinMouseSensitivity, MaxMouseSensitivity);
                if (Mathf.Approximately(clamped, _mouseSensitivity))
                {
                    return;
                }

                _mouseSensitivity = clamped;
                PlayerPrefs.SetFloat(MouseSensitivityKey, clamped);
                Changed?.Invoke();
            }
        }

        /// <summary>Moving the mouse up looks down, as in a flight sim.</summary>
        public static bool InvertLook
        {
            get
            {
                Load();
                return _invertLook;
            }

            set
            {
                Load();
                if (value == _invertLook)
                {
                    return;
                }

                _invertLook = value;
                PlayerPrefs.SetInt(InvertLookKey, value ? 1 : 0);
                Changed?.Invoke();
            }
        }

        /// <summary>Mouse movement as degrees to turn: x to the right, y up, with the sensitivity and inversion applied.</summary>
        public static Vector2 LookDegrees(Vector2 mouseMovement)
        {
            var degrees = mouseMovement * (LookDegreesPerUnit * MouseSensitivity);
            if (InvertLook)
            {
                degrees.y = -degrees.y;
            }

            return degrees;
        }

        public static void ResetToDefaults()
        {
            MouseSensitivity = DefaultMouseSensitivity;
            InvertLook = false;
        }

        /// <summary>Writes the settings to disk now.</summary>
        public static void Save() => PlayerPrefs.Save();

        private static void Load()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            _mouseSensitivity = Mathf.Clamp(PlayerPrefs.GetFloat(MouseSensitivityKey, DefaultMouseSensitivity), MinMouseSensitivity, MaxMouseSensitivity);
            _invertLook = PlayerPrefs.GetInt(InvertLookKey, 0) != 0;
        }
    }
}
