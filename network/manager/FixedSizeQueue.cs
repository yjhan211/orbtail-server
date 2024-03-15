using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace network.manager
{
    public class FixedSizeQueue<T>
    {
        private readonly T[] array;
        private int front;
        private int rear;
        private int count;
        private Action<T> releaseAction;

        public FixedSizeQueue(int size = 20, Action<T> releaseAction = null)
        {
            array = new T[size];
            front = 0;
            rear = -1;
            count = 0;
            this.releaseAction = releaseAction;
        }

        public int Count => count;
        public bool IsEmpty => count == 0;
        public bool IsFull => count == array.Length;

        public void Enqueue(T item)
        {
            if (IsFull)
            {
                Dequeue();
            }

            rear = (rear + 1) % array.Length;
            array[rear] = item;
            count++;
        }

        public T Dequeue()
        {
            if (IsEmpty)
            {
                throw new InvalidOperationException("Queue is empty");
            }

            T item = array[front];
            releaseAction?.Invoke(item); // 릴리즈 액션 호출
            array[front] = default; // 참조 해제
            front = (front + 1) % array.Length;
            count--;
            return item;
        }

        public T Peek()
        {
            if (IsEmpty)
            {
                throw new InvalidOperationException("Queue is empty");
            }

            return array[front];
        }

        public List<T> ToList()
        {
            List<T> result = new List<T>(count);
            if (count > 0)
            {
                if (front <= rear)
                {
                    result.AddRange(array[front..(rear + 1)]);
                }
                else
                {
                    result.AddRange(array[front..]);
                    result.AddRange(array[..(rear + 1)]);
                }
            }
            return result;
        }
    }
}
