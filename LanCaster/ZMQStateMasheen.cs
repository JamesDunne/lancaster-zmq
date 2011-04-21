using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ZMQ;

namespace WellDunne.LanCaster
{
    public sealed class ZMQStateMasheen<Tstate> where Tstate : struct
    {
        public sealed class State
        {
            public State(Tstate state, Func<Socket, IOMultiPlex, Tstate> handler)
            {
                this.MatchState = state;
                this.Handler = handler;
            }

            public Tstate MatchState { get; private set; }
            public Func<Socket, IOMultiPlex, Tstate> Handler { get; private set; }
        }

        private Tstate currentState;
        private Dictionary<Tstate, State> states;

        internal Tstate CurrentState { get { return currentState; } set { currentState = value; } }

        public ZMQStateMasheen(Tstate initial, params State[] states)
        {
            this.currentState = initial;
            this.states = states.ToDictionary(st => st.MatchState);
        }

        public void StateMasheen(Socket socket, IOMultiPlex revents)
        {
            if (!states.ContainsKey(currentState))
                throw new InvalidOperationException(String.Format("State {0} not declared in state machine!", currentState));

            State st = states[currentState];
            currentState = st.Handler(socket, revents);
        }
    }
}
