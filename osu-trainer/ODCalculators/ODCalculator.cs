using FsBeatmapProcessor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace osu_trainer.ODCalculators
{
    internal class ODCalculator
    {
        private static readonly ODCalculator standartODCalculator = new ODCalculator(79.5M, 6M);
        private static readonly ODCalculator taikoODCalculator = new ODCalculator(49.5M, 3M);
        private static readonly ODCalculator catchODCalculator = new ODCalculator(0M, 0M);
        private static readonly ODCalculator maniaODCalculator = new ODCalculator(63.5M, 3M);

        private readonly decimal _offset;
        private readonly decimal _multiplier;

        public decimal OverallDifficultyToMs(decimal od)
        {
            return _offset - od * _multiplier;
        }
        public decimal MsToOverallDifficulty(decimal ms)
        {
            if (_multiplier == 0)
                return 0;
            return (_offset - ms) / _multiplier;
        }

        public static ODCalculator GetCalculatorForGamemode(GameMode mode)
        {
            switch (mode)
            {
                case GameMode.osu:
                    return standartODCalculator;
                case GameMode.Taiko:
                    return taikoODCalculator;
                case GameMode.CatchtheBeat:
                    return catchODCalculator;
                case GameMode.Mania:
                    return maniaODCalculator;
                default:
                    throw new ArgumentException(nameof(mode));
            }
        }

        private ODCalculator(decimal offset, decimal multiplier)
        {
            _offset  = offset;
            _multiplier = multiplier;
        }

    }
}
