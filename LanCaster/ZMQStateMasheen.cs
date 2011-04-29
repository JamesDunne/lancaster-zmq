using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ZMQ;

namespace WellDunne.LanCaster
{
    public sealed class ZMQStateMasheen<Tstate> where Tstate : struct, IComparable
    {
        public sealed class State
        {
            public State(Tstate state, Func<Socket, IOMultiPlex, MoveOperation> handler)
            {
                this.MatchState = state;
                this.Handler = handler;
            }

            public Tstate MatchState { get; private set; }
            public Func<Socket, IOMultiPlex, MoveOperation> Handler { get; private set; }
        }

        public struct MoveOperation
        {
            public readonly Tstate NextState;
            public readonly bool Immediately;

            public MoveOperation(Tstate nextState, bool immediately)
            {
                NextState = nextState;
                Immediately = immediately;
            }

            public static implicit operator MoveOperation(Tstate nextState)
            {
                return new MoveOperation(nextState, false);
            }
        }

        private Tstate currentState;
        private Dictionary<Tstate, State> states;

        internal Tstate CurrentState
        {
            get { return currentState; }
            set { currentState = value; }
        }

        public ZMQStateMasheen(Tstate initial, params State[] states)
        {
            this.states = states.ToDictionary(st => st.MatchState);
            this.currentState = initial;
        }

        public void StateMasheen(Socket socket, IOMultiPlex revents)
        {
            //if (!states.ContainsKey(currentState))
            //    throw new InvalidOperationException(String.Format("State {0} not declared in state machine!", currentState));

            MoveOperation op;
            do
            {
                op = this.states[currentState].Handler(socket, revents);
                currentState = op.NextState;
            } while (op.Immediately);
        }
    }
}
