using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;
using UnityEngine;

namespace ToysBadgeMod
{
    internal static class ToysIconData
    {
        public const int Width = 96;
        public const int Height = 96;

        private const string DeflatedRgbaBase64 =
            "7X0HXFRn1j4zDL0KCCIiSJMOgoJi1xh7bIm9xN57FxUL2Av2Ems0scQSEzX2Cgoq2EUBFRQpKoqodHj+z3tnBpHVlP3v7rf7fevv"
            + "d5xhZu6de5/zvqefMxoa//3333+f/xefmGRN8ie1IPUgDY1/mDwhPjE5mBSiouCExOQJpKGkHqQWqmOs/4vgn/+XkJhkQAog9SbN"
            + "JX47SedJCaQ3JCgp+Y/ojeqY86pziHP1JgWQDP6L9EeYW3K9tiGFkA6SEkgQdOjQESxZtAzDhwzHV63aIsCvJhyr2qGiiRkMtHWh"
            + "LdOEpkwL2tr6MDUxh11VB/j5BaB1q3YYzGMW8tifeY4PfJF4cpDEPZPUhmT5fxj3BglKuXGUlCnw3r1rH0aPHIe6gXVhSEx1NRTQ"
            + "I+lraJLkMNCQkTRgKD3K+L4MmnxdQ5CsHPE9NWmTVwGBQRgxcgx+3PWTmh+ZpKMqOdbg/5B86UTaQJLW+fff/4ju3XvDsqI15DJt"
            + "kg7kGtpQaGhBi6RD/HWJr54KeyOSsepRj6RJ0pCrSFNF4rmsDJXyQskrC4tK6NqtN7Z/v0vNC3EtvKZkXluywf9C3HVIXUg7SZkx"
            + "sbcwacpUODk7QUasZHLiItckaZG0pUcZ5YqM615OvDTFGibOuiR9af1rSLzQJbZygbWiHGmW4clHvBA84PeQr2pydHTFpMnTERN7"
            + "W70nqC+Su5B0/pfYLy2I+SaB+8XIKPQbOBDautof1qogBfFXKFREXDS1lLygfJcwkynXuYKko1r3gnTE64J/ChVpqqgc7jLpUa48"
            + "n4Q7v19DR0XaEmlrG6Bf/8G4GBGt5sMmUov/YNxdlXZHcsLN23EYPGw4MRI4y8qQvAz22h9IU1u5D2SKDzzgHpCRFNJe+EBackHy"
            + "DyQTJPQyibJLk9hqagiZpk8y4DkMeC5B+irSU/FBS9obMh4ndLe4ZpW+FraT638Y9l1Uug2h8xbBwsqGmOoocdUsg6/6UcJa52Mq"
            + "xURRSkIeKWWSwFaT+kGT+0GLe0GbckmHj7okPZIhXzcif0xIpqQK/KwZeWfG48x4fAWex5jnNFLxQFsplwSfVXvHwrIir32Byt5N"
            + "Oiru6T/EVxK23ZPjJ8+iUeMveS+8P03eqybvVSbuVVeJrYSzGl/NMqQo86iybVS6U6Yi9WcUfK7LR0P+bUQ+GBJHA57fkOtbj9jq"
            + "EX89Yq2rYUF+WJAPFXmMRRn8DVXrX/VdZXW4iho1aYLjJ88IHjxR3Zv1vyn2wrfZJtZLaNgCaOvocS8Ta5nA3YT3Zgo5+aCl4LpU"
            + "6FM+aHNNyqHPezYmGcmUetWEZCpTPgobR7xuxPcN5Er9q5BsHjm0KXcM+VoFvm9FshaPfM+CZE4yI1/MyY8KxNeE/DCU+GFMHvD7"
            + "eU1ackPyQZfXSNnEfSjTlCsx15JBrks+68hK9bi2trZ0T6q9IO4x4N8Me/oyySeEvOnarQfviXY5daimgvJW4M97lsmNoatjCnMj"
            + "c1Q0NIGxpgKmxMlBT45aVroIqqyNAEsN1LHSQFAl5WMtKxlq2+oi0N4QHpW0YaUrk+wfwYcK2jLYm2jAh5+rW0UD9UlB1hqoWVED"
            + "NSw04FdJBzVtjOBtaQR7fW1UpFw3Ik/EfjFQ6NB/06PupgzT0YGOgQHkOpoS3nId8tZUFwojbaUtpdbj/E5xbyoenBD3/O9hW0rx"
            + "lmsRkdGoXTtIsjUE/nLem0JLn2QKTa0K5AXXn7YhzPQNUFFXj+tWzrVJnCroYUQTd4T3a4QV/RpgUfeamN+lBhbwcUHvICwb2hxh"
            + "A7/EgJbe8LM1kfaFwL8ieVHP3QTju3hhyZBaWNDPG/P7+SKsXwDCBtRD2OAmmD+sNcZ2roemnlaoqqfcU2KPGVLn61HnK4itjh71"
            + "hwn3o54SfxnxV/CaFBX0IdOlXNKUleIvKCCwNm2kKMGDayIe9T+K/cPkfqS4X379DQ6OjsRcrrT1VNcqp87V0jWFQo96T8uAe14h"
            + "2Y/Cf7LkvQr8AyqaIHxgC8RsGoPb309CzMaRuLRqECJWD0bEhlG4+uNMnN48FUvGdUITb1tJJgkZZEo82zawxY9LuuHq7qE4/113"
            + "XNzaB5d/GI7Lu8bh/PfjcHHXdOxYNADdv3BCNWOZJKsqcE0bkbTEeXgN2sRbi9jLtGXK9S4e9amXDCmb9ISNIC+9HzVVZQxEGddI"
            + "iiP1+x9a9wL7hN179sPc3FyFvezjaxV+FO1qiRRayn0hZDzXnZ0BecDntSoaY8WQFojaOAI3to9D3K4puMnHiA3DcY74X/4hBKe2"
            + "TcfCCV1Qz8tOwl6c24D4t2xYFT8u74Ure4bj7KZuOL+1Ny7tGoaLP4zEbxsG4+S2cdi6oBe+aWQDW35fBX6vuY5Y/yp/QsibsjJG"
            + "wl+uJC2ufS3Nj9Z+WTIxMVXFMiQbtd+/Vt5LMidu9559MDY2+Qh3GeWKpibXk6bKn1XQvtClDtCl7SNXriWFSg6INehqpIE5fZrg"
            + "9PqxiNo2FZe2TMbZdWNwfPUYHFvH59/Px6GNczBjVA/UqF619HsU1JPNGrjjx5VjELVnBo6sHoBDK/rhGPfPiS0T8euGcTi2LRhb"
            + "5g9C56ZOqKynUcq7T5JMpXspj7SMKH/0aT9oKz6LvyB9ylIVD+L+VbIoXhmrvHbol6MwMzP/CHuxB7S0GLOhTtPUEvY+7R9NQ8pR"
            + "C2gYWPK+KsK2mgsCatZCkF8N1PX2RYeGgZjavzO+mzMO28MmYfWUYQifMBhrg8dhw+zp2LpoIVbMCcOQ3gPh5eoPHZ5LV88KpmZO"
            + "aFyvFRZPmY1dC5Zh/eRpWDF+AtZOnYbNc+Zic9hcYh+GlTOmYfy3PdG+YT3U8/ZCoLcPAny8UauGN2oH+iKoXk0E1vGHh48bzCvR"
            + "R6Cu1jE1pA7mdesoVL7f53kg9oFKFl37Z+tkYs/YfPIJoWuFvJfWOa9PqW/lkp2mq6tLIv60MSTbX2bKPVCJ698W5rY10HdoMH46"
            + "eA4nTl5DxLnrOPTTCUyfMAddv+qJjl92RqcvuqJry77o0XYwun01HD07jkO3jpPQ6stRqOHbA47OHVDdvSu8fPuhTs0RaBM0Fb0a"
            + "h6J/s/no22wuvm0+C9+2nIlezSehS5ORGN5tBr5btBu/7TqH0wcjcOFYNCJOR+HcyfM48dtxHD9+DAcO7KWvNRu169aCJvWBsIW0"
            + "JH38+9iX1QcqnXzin2WbKn2r5G0iZhkUVFfCXmAuSEFbQmAv1r14FH/LpLgB7QotC8oi4q9pBQvbWpgf/iOy81H679GT9xg7cTW8"
            + "fbvAy6szavn3Q/2gkagTOAI+XgPh6T4Qvj4j+fokBAQEo07QbNRrMI+2Vih83WbB134u6rmsxJfe36Gxx0rUc1uIum6zEegyGW7W"
            + "/dC+yWz88kM83mfwywrwyX+5Oe9x4fwZdOrUToofCUz1aBfr6mjx/v4cD4RdVMY/sP4n4C98P8aKe34kcwQfBO5i3QvZo35PuSfo"
            + "A4g9IMUSGK/Xt8aceavwvqC49N7vPkxFr8GLYVu9O6q590F1n2HwDZiIGrWmwMNrHNw8xsLbawpq+s5E7ZqhqBe4AI2CFqO2bxhq"
            + "uIYhwHMVGtT6HvX9t6GG2wp4u8yDj+tsuDlNQEXTLqhfexL2/vgAOdmqL+RXlxQpH1GkfKkwvwDRlyLQsV3bD/qFcl9bIf/T+Jfz"
            + "D0L+8fGcpCfCB1TLm/L4C+zl8g+2mi5jnCYGujCiDaGteq2Cvi5mz5yJrKzsUvzvEP9vR4SjinsvWLn0ho3bILjUGA/vgBnwrTUT"
            + "gbXncD/MR4OAMAR5z0SQRzCa+IaQ+HqNeQistRT+tVfCq+ZiOHuFwN1/JmrUng5XnxEws26LwPrDsGXHZbzIKoKa6wWFQF5eEfLe"
            + "56OIf2S/eo1zJ06iQ+sP+Ouo4kvy0vjHn+OByk9+8o+KF6nimEdFDESbOAvZIjAvXScq2fMBezn5oQtzM1P4ejiiW5uGGNWnPQZ0"
            + "a40BnVti747v8P7dOwkLwoCHaVlYufUsBk74Hv3H78KACfvRfeguNG1HmRQwC14+wahbaw6aB85BK99gtHWfiM7cD13F85ohqFtn"
            + "LlwDQmDnPxmudaehVY+VGDZ1L8aE7EH/saswdd5WnLh0H+nvivCOX5pXUoJ3hQV4m5PD63iPvHdvkfU8A3evxWJZ6Hx07/A1+nTu"
            + "if5d+6NxUBOYGJmWwV/2N7mcD38r8RBYqOJFR/8RcVNV/JWxtCalOlZgXnb9S39LcXqxN5gv1DeETeXK6Na5HY7s34yncRcQd+Uo"
            + "Ys7+gifxd1BUWAQhgnKJ//P3RbiZ8BaXr79B7P1CxDwAvj/0GD2G/QDnGlNRqepI1HSfhE61pmGQ/0SM8xyJmR7DMZ00wHscmlIn"
            + "ONbk5/zGwafZdEwK+4V4Z+BG4jvEPniBW4+e49nbArzi972ivMkqAV4X5iGLAul9ThbXfireZDyhfkjF49u3cO1CJO5cuY27MXFY"
            + "sWQt7GmvKe1TYVMr40Uf5RBkytitjDFcmVwh8UJgpZJDc/9/cyfCvxB7qqyNKelWEYuUyaU4g0ymXAuamtpSDlE82laxw7hxY5CQ"
            + "cEdoOOS+yUD+m1coePOW+74AuUUlEv7vuAlevwHeUiQVEpt80qmoh+gzehPsa0yEVbUxqO89DQNrjUeIzwCsdOqCH+w74QeHLpjj"
            + "OgDt+BnnWtNRyX8CfJtMxfK1p5D2nOcqVlIez/e2RIk7v0ai10V5yMx9haw3z/AiNQ6ZT++g8NVTlLx7ieLcd5JeKOaxB34+juqe"
            + "fipfkvpNn368TgVl/kDEteTGUmxXpjDm6yZQCD9Trigrh4Rv1uLvxF6HtEnkISwsKn603rW0GLvU0iklKdZGXavD71cIm5+8qFTJ"
            + "FhNpk6dmvlSqPXFDJUqhU5SvVH1CBhXzteL3fFLGJrp45Rb6jV8Dh9qTUdl1CmV9MEb6DcNyt6+xt+oXuFi5MSKqNMcGh27o4jEa"
            + "1SmHqnIf1P4yGNu2XCCGH9s3RarTC/Mnj9/6tugdst5x3WfG482z63iXcYtL5Cnfzf7ouGNno+DmXesD/gYVyQNLyLXMiTcftS1p"
            + "W5txXzC2q2WiijdqSXtAYKbK4Yjcn87fqXMzBw8ZVsa3lan8K12SXimJ+gKBu46WGn8FKphZoc+AIYi6fRuZWVl49OgR0uIT8D41"
            + "DUXZr1H4lnvh3SsUZb1ASdozCiIu2tz3KHibhdNnI6gHVsG14UzYeoagodc0DPXojxXObfCLbX3EWNdGrHV9bLHrgC5uw1CdOtc+"
            + "MBj1v5yCbWt/Q86zHBS/eYeS15kUOukoeZmC4tfPUJKdytefID8zAXlpt5D/NBrFKZeAF7EoehOHN+m3kfEkDum8lmfUyZt3H4Sj"
            + "q6fq/hmvY/xQxLO0dc1py1nxb65LuYkqn6arkksf9PUQYicwTPiLuljUKIm6JeFTlI/pKGPLzD8xfiznXlMotCX85XJV7YKUN9eC"
            + "kbEZGn3ZEgtWrsb23buwfu0K7F6zFLGHd+H59dNIv34MqbGH8TLmCN5EHcH7q2fw7u5VpN2JwT7GlHoPWwiPhrNRzTsM9T2mY2D1"
            + "fljs2AoH7eriqrU/rlYKwHdVW6GzsJf8g1GVuiGgzjAsDd6IxNPX8CIiEm8uHMf7iCN4e+EQ3lw8iLeXDuB99AHkx/6Mohs/o+TG"
            + "fiDuF5QkHENG7CEc370Ca8LnInztWizauBW9R4xGJdsqvB8RK2Huk3JXm7JVn/vcQIe5DE2Rz6Tsl3SBGvsyeWfariq/bOdfqfvi"
            + "ZzsJvvXrP/CTNpbQAUobSC7hr8vYjrD1lTlCLWkPGBpWgIOLGxo2b45W7dugVdPa+LZFALZM7YOYnXNwbcdURG0Zg+tbxuPepqm4"
            + "/91sxP0Qjiv7tmLd4qX4usskeNaZDhfv+WhEu3Kg+yAscWuPn50bIbqqP6Jsa2KTYwv08BwEL+pmxxqjEeDbGzMHhODC+h24tWEN"
            + "4teEImnDbNJ0PNwwBQnrxuL+mpF4umMK3h6ej/xTy4Bo2mOXvsPl70MwoVdLBNRwh09ATXgE1YOdW3WYW7C2S1/kGxjvkSnzQKa0"
            + "qY24/nRkyhycppQPpS4U9ofGx7UvAkOBpcD0L+C/4RprRIStUx57sf7Vvq7QvWLtC3tH6AApfy32qULEIAxgZGoKC2tLVKpsCjtz"
            + "OfMkmgjr6odzS3rgwpJ2OBP2BS6FtcS10A6IntkVkfOG4vTKECybOAEdmtPv9RsPL++5aEa7f7DnYCzx6ICfXBrgtI0nTlVywzq7"
            + "+ujr1gtB/uPgV2MI9URXzO49FicWLUHkgmmImjsIsWHf4vr87rg2/2tEzGqJM9MaI3ZpB2TsHYHcE9OBK8uRdW4RflnyLVr7O0jx"
            + "2bJr2FhXA5UYO62o9aH2yFxTGcNV54J0hM0tk0k+W/njtXW0IbAUmP5J7BsIvT1p8tTPrn09PT2JhPwR+le5/hUf8rRCBnFt6Ohq"
            + "wcyCtqiVDhzNmEtljmpJNzdcXd4O15c3xY1FdfBgSUM8WtQSD+a2x+0F/XAlfDI2jh2OHk17op7vSAR6zkBL2vyDXftggVtrbHcM"
            + "xD7LathjaoMlFb3Rz641vvT4Fk29eqKTfwcs7zscF5eEInoJ46lhPXGVuF9d3A6RC7/EqblBpNq4sb4VXh4ZhKLIKcC9pXh3ZQGO"
            + "hnfHV/62Uky21NZQxWnNZcq8pqmKzFSPUh2SyJtSzujJZVJ8tfz6F7FtUesk6kD+TJ0dsQ8Wtqujo9Mn8VfrYLEHBMZi3QseKHWB"
            + "eK7UxVp8bmlhjBqetmjob40vPPTR018HmwZ64taq1ohb0xiJq+ogZXVDpC1rjqSwNni8uA8eMeZ8cPJQjGn2DTrW6I+WbqPxjSv/"
            + "9uyONYFf45c6rXDcOxBHq/tig2MdjKzSFF9X+wqdq7fFwBrN8d23/Yn3dNxYNgJXF3bFtWXtEbu2Ha4R88g1jRC5tgFu/9AKacd6"
            + "4t3FYSi8MQNvI6fh7OpvMLBRFbhQ1tgbyeBgpgUHUwWqmyngVVEh5TNrVDaAo7GWxAsT1V4wJsamjFMYMFanpcZf42P8HZ2d1XV2"
            + "wX+AvaXw27azJvBz/rVa/wqfS8gfoYe1tXUkGWRuXhFWVtb0fS1hbWWFJnW8MW1IW8qTtswR1sb6YTVxamFrJOzsiYc/fo2kH9vj"
            + "2faOSF7VDvGhXyFpYQ9krB2NC5N6YOEXTTHal5hW74KxHt2xolFfnBrAdRS8EI+mhOHh+Dk4130cFnm2x1ibJpjg0BgzPOphb9eu"
            + "uD13HO4uHIzY+d/g5oqOiP++Ox4f6IWEA11wf397JB7qiNRj3fD6dF/kR47Gu7NjcW9rT6z/1gsja5tjVP1qGNnEA6Oa+2Ba19qY"
            + "+W0QQvrXR3C/xmhf2xF2+vLSXKYSf5kSf1k5/Etr8GTYvkOqdRQ1OZa/n0dPyhRxpM9hr443iD0gsBdrXuQhLC0t4eLiAnd3Tzg4"
            + "OMPHwxUT+rdHzP65SL+4EMmHJiDp5/FIPzEDmRdnIz0iGM8jpyL92EQ83DIYdxf1wKOFPZG+pA+uDW+CbQ3csMyrBkKdgrDSpwWO"
            + "dx2KlPU/IP/yHRTEJCE/OhnP1h/GT437YaV1Hay1r40NbrVw+uuOeDRjBB6GDcTt0C64t6ornu0fTBk/Hm8ix+B1xDC8Oj8A2aT8"
            + "yOEovjIBRZenIOfwGNxf+jUuTvgSV6Z1RtSMvriydBQSDy7CgxMrcPPYckQfWoZZQ9vCk3vBRFULKdUDUx/oylW1qLJytY+qOpau"
            + "PXqq6+ra/FGMs6y/9Sn8ldhrltpBIvZpyXolNzc3eHh4oaq9PWr6uDNn2B2vIlcyyLwBuL4QJdeX4+3VFciIXoLUG0vx4u4KpEYu"
            + "xt0dE3FtUX88mNcLafM64/agWjhU3wY/eNphbdVq2Ormi6hefZHNPAde5wE5InBBX3ZPJI407Y3NZu7YYe2KH5zccblDW6QS/6fz"
            + "huBOaHfcXdkdaT+Poo0zHXkx05EfMxk5l0cil3xA9FjSeOD8OODwKGRv7IX0xZ2RFT4Ar1aNQtaPc3jdu5AXT/s1/jAy7/+GVTP7"
            + "wbWSVqnuNeS6NxAxxrJx0k/gb0F5oJJBIb9j8x8U+bQ/kj2f8glMTEzg5OQEZ5fqqGRtDc/q1RAyvAOSTy4Ebi9H7uU5yIlZhudR"
            + "4XgcsRBJ1xYh9eYyPD6/ADHbJiBiXn/cnNMTT0I7496wujjxhT32elbGWisLbHJwRER3yotDPxP/d0r8n+Tg5eZD2BPUCuHG1viO"
            + "/vb26i44/3VrPJw5HAkLhuPGwr64tbYvkg+MRtbFYORcnYHca1PwLnIUfYKhKLg0EiWRIxnwGILcff3wbFV7xM9rSTnYCQ8X90DS"
            + "5jF4dXEt0m/8gGe3DyLl9mEsmvYtqlEfaAvbRuwByn592jjaWtrKOuIyNXQf1XLxvR93S7Xv7D/42xprVf1UwoiRo/8Qf3WsU/0o"
            + "XrO2roxatWqhfoOGqBUQgHp1/DB1WCdcPUj5c2E+Eo9OxZNz85EStRIptPmexSxE6tVQPDoVghtc/1HUlzcosx8t7I/4aZ0Q0a0u"
            + "9gd54TvWSO8KCETM8KHIPkz8n9FXfv0WSMrA8+17satlW4S7OGNrLV/80LQuTg3qghuh9CuWjcMN5oDjfpyAR4cmIvMCsY+djdyr"
            + "U6lvR9E3G4x3pMKokSjm31mHB+D+hnbU3c0Qu6Q1ri7tiGsbB+DO4VBcP7sGV89uxvmjGzB5xNdwtDGAjqZS1gubX04fSKFjwJgE"
            + "a1ak2mG5MndWrh5e9B+o7KCAT+QWe4v9IfI4v4e/kP3C9hTrvVKlSswBm6FKlSpo2bIlpk+fjrX0HdesWYNVKxZj5cKJWDSlE+aN"
            + "bIrFY+ph4+y2OLljNBIvhOH51TC8ujobmZHzkX5mBZ4dXYf0w+vx6sg6vNofjpQNYbg3dwZiJ03DvZC5yFizGjm/7ENOxBlkR0Ui"
            + "+8I5vPxpD26GL6KdOYO8C8HtpXOQuH0Fnh7ajJRfN1DXrMeLc6uQfjoMWZGhlD2hyIui/IkYSRoqUdG1sSiKmYiXJ4bi5raOuBD+"
            + "JS6uaImItR2xf2EHzBn+JUb2a46xw1phWP8vGZN2hbmJLuv3NErj7aJ+VcbYhFyHOWMp76qprFsph39AbXWOLLn3J/AXtcqSLfNH"
            + "+BsbG8POzo75Qi9Uq1YNPsxnT5w4EZGREcjMzGR+5TXS05Kxd9daNG9UDa60+xv4yNG+vilWTGuJ+ydnIPvaXORdm4nC2KWMATAO"
            + "kHiReuIK8Pgq8CAKuMXnsTcpfxmQvsYY6rlzeHdwN1K2r0PSlpVI37EO74/uQ4n4XOJdyrhYfjaax/KYJ/z7IV9POIfie4eQe2Ud"
            + "ciMXoCBiBgrOjUHhuaEopu1ZfGkUzz0JRVcm48WxYbi59WtcWN4C58JbIWL9N1g1rhFq2RtJ9SqVzFnjSDKmfarFta2poBzQVChr"
            + "WGWqXLdCX1lzIGIBmpof9oAKf4Hd5+LSou9A5PE/5BXK0sf+l5GRkSTr/fz8YGtrA1dXF0ybxhjC/XulscNsxte2fb8a/jVtUJH1"
            + "gJ4uMtTxUGAx7YtnlP9Ft+ej8PIk5F8KpQ90gJhdB54+QMmTByhMvI+iBw/JjxdAOvXtM0aNb9zCywO7cG/1fNwPn4kXWxaj+CT5"
            + "9uC2UiY9fkK8k3mONMbTqJyf83k6ryfpPEpityP/LNf/8fEo+m0oSk4MAs4MBS4Qf15D0cVJePHLENze2BkRS9rg/CJSeEcsHhQE"
            + "NzOd0poZdQ+CHutDjYwZe9TTV9Z1y1hjw1iQhsJQyQe5qP1QqPaA7KMaRoGx6EMph73IrZ8X/Wp/hL/YA6aMKwhbJzAwgLamHW0e"
            + "N8qeaUhMjC/FPyf3Dft71rBOwRrGrNN0tmOtpoMG5o36AqkXF3N9L0TehYm0A0NRdPcoMXwEZDxH4fMXyE1JR27ScxQnZQEpOSh5"
            + "/JJ75TqeHPgR19fMw63lwZRPoXh/aAeKY7jm4x8D9xk/jktFycMXKH7GOH4a+ZDBcz7h+zd2I48yKOfIGBQfIe4Cf+pcnB7BfUWe"
            + "nBqPF/uH4N6G7oim3L+0qAN96E5Y0q82PCtol/rCMtWjnvC39JUxGLHe5VqGUo2fQp/1OIIH0n7Q+iT+ixYtFfifTyiToyf2/kIv"
            + "iL6DP4u/WP++vkL+2MHVzQUDBvTDkSO/IDn5MR49TkBsTCQ2r5+Hb9p4o5G/BdrWM8M3Taqwpqcn0i6vp1z4Dm+jliAjchNex13D"
            + "27Q3eP08HxkZb5GRmonslOcoSk6nbGHc+B7rF65EIfXEz0jct5m+2zqk/LgBr48eZBzzBj+TwrWfgcL4F3jLnNfLhDS8fPQU2U8f"
            + "IS85BnmMcb6LZt7/fAiKLkwCImh3XhiN4tOjUHxmHPIE/r+MxMMdAxkL7EM+9MbNdb3w/YSm6BxYBTVsTRHgao4AN0s4VjKEMWNB"
            + "BpRJupqqOmPGQ7UMzKFNkgv81XkxqadK9iEeSvyGMiat7H1L8i+DfwvRM9uKfZsf+h0+jb+yNkNPqjm0sqpIXhgzRlgBvjW8GOtj"
            + "/HFmMCZOGoexowZgHmOdP9CfOrS8G/bM74C9S/sj9shq5D46yZj/adrUv+LxjbO4H/cEd5KKcf0R6xDupeJmXALSHyagMIn76VEc"
            + "1/dNFN27itybkXgfE4Gca1HcD9dQcDeO8oqyJ+0VSlLf4G1KNpISX+JWXApzmUm48ygRScm3KIrOIStxL3LiyPc46ps7c1F8bRre"
            + "XxiPd+cn0hadgszTk5F2eDzSDo3FS9KzvcNwZX1P7A1rh7XT2uC70G6sBRuAgZ245ylPLXSVNdh6IhYv9K8mY2Caot5JV5WT1FL1"
            + "78hL177Av02rNgL/Nwll8mKinlDoXtE7+0f4K21QpQ+mUIjaB/Y8sM7BxNSA+8CRtqcfPDxdEehfHSEj2+Lhidn0e0Lx/OQ0PI+g"
            + "zrx/gnIhBsi8ifdpN6W8TFT8O5wi1MeY/z1x9zmi7z9CyuN4FAk9+pS5qadc48mkx7coX+6jJJGynvIJT1+i6Fk6CtLSkfP8NcXX"
            + "e9xKZi3D/TScvJ+CMwlJuJZ8H0npsXiVcRa56YdQkr6T51qDwttheH1pKl5FTGX+YSayL81C9sWZyCEVXg5BztnJeHVsHNJPTcbD"
            + "41PxNHIJnsduwgrac24Cf8ajzRl3MJbLpJp2LdG7qe4n+xv8ZaX4+/v5Q9Xf3KMM/kMF/lXZv/x7+ItaGGXs7UOtiXiup8+6K11N"
            + "WFYyp+9rS1/PAq5O1pgxtCVSztKHvBWG3PPBKLy+hfidRsnTy9SNMSh8+QDpGS8R/bgYh4n/Iarc408KEZWWjUfpqchJfUg5HqfU"
            + "o+lxzFVRLz9+iELWrJQkveTaJ/bJD5GTkoCszHSkZufi5os8nE3KxvHHb3A88Tkik5KRmJaIrBc3UfAyAnhJXfNkJwpuLadvNYux"
            + "kBC8i5qD91GzkXOJ9tHlaUAMKXoCfTPGJqJpq12ajfybK1AYtw1rJ3WCk54yFm2ijsMRYwPKe20pB/Wht7K0f60MhnaslVP5wUPL"
            + "4C/mKdCmN/8M/h/8LAMDfcZ72LugWbbOinURrNmzqWLFWJ8DrG2sUN3RChP7NkTCYcrbK4wxnprI2M1qlNw/zHV8lhhc4B64g7ev"
            + "X+F6UgkOEeY9CcA+Lu3jGYWIff4GaRlpXLOPUZSRiOLnj1GckUw5Q32Qolz7SHmKYubPczLu4NXbp0jNz8OtN8U4m5pPPhbgSHw2"
            + "zj5IR3xyKt6mi+Npx2ZEUqb9jMJr65F1Zj7jQnOJO3U5bdOss+Pw9izt0kjq5sj+1BPU01Fj6SdPobziGrqyFGtHtICnnjIerSZz"
            + "0cMjbHNVLkZW2j+l+Bs8Tdi7r8J/Qhn8g5W2v56q90r+iVoXJc7qGkPRd6uO9elQDlWrWgnNvwhC985t8XX7pujWoR7CpzIWdnQK"
            + "SqJC8P4UY1znlqLw7kHuf+L/8BRtHurd9AxcvpuD7y8XYOXlfCy9+g5bE3Jx7nkhkt7k4O2rF3iXnozs1EfcD8QwLZXHEf9k2jdJ"
            + "tFWfXsWrpxf5ZyxuZzzFqccZ2HM7Az/ceI3vo57jQFQKrsel43VKGkoyaCelUY4x31gUvQXvzizGu3PzkBcRijdnpyCNOYH0Q93w"
            + "/lhn5B/rgLzj36Do3ED6CuNQcnkmck/PxK7RTdDRTgt1TWTwIx88qAOcWKdbkbaOiEfIpT5Nda9rWfyV/W0C43il/Akug7+YIaKq"
            + "K/lUrZd6D2iqclyMP3PP6Yh1T91S2UQHbRr5YGFwf+zbFIIDm6bgwPqRuLx7PF7SviyOXoj88/OJ/0oUxHH9p9K/ekhZkHyHJuIL"
            + "/HwuE2H7XmPsrpcYtvspQs+9whHug4cMM2S9y8HrjGd4+SQeb0mFz2jrPH0u2TyFcbHIunMUyTd3486dX3HhbjS2XbyKsENXMetg"
            + "PEL2JWDFr/E4FZuCJ09p03L/FKdxDySe5vrfRtt3Ad4zFptH+fLy5AQk7u2N+1vbIGl7M6Tu/AKZ+9oh7+QAlDA+V3JxBkqOhyBy"
            + "aiss9DPB5Co6GFvZGH2sKqCxkQHsFAqpJ1nZR0AMteRKH0CuVaZeiHEK2qYq+R9SHn91/lJWPo9Tir3yPJrsadPXZAyC+0Dk4dzY"
            + "vzW0Yy0c20R9FbUKWdeW4BX1WVbkLLyLmE87Ywlj7Mtp/61D4QPK3/TrKCb2xdSbjxNzsfFAGoatZG3pskfosvwBJu5Nx55bxbhL"
            + "EZNJmf7mBeVHSjzynsXTbiL+KZT91AE5N6ORFrmD8YwVuBezE5G3zmH90ZMYtf4IBq67gr5rbmLyzlv46UoS7qWk4uXzR8hPvSXJ"
            + "v/ybW/CSmL65NIkxoWCkHx+LO9t7IZq5oKh5Qbi1tD6e7eiE3FMjUHx2IkpOzwBOLsCDGV1wwM8aP9hUwAHG+za4uqGHkTGqy2RS"
            + "H6GMellDXxB5oK3qa5Zqtli7pSFmhujRT0rGw4/xl3JeOkKuaChzmJ/CXy5nrQnrXHQU7OvkcyPqA9Gb6GaujdEdvBC9g/5MXDjt"
            + "xXmMB0wFrk5DzvnZeHVyPjJPLMbb86tQeJ/yJ+MacUxA8cu3uB9fgPAfH6Pn7Bi0Db6C9nOuYdzWx/gx6j1upxXgdXYWcjIfoyD1"
            + "Nv0qxhaoS5HKmhL6uXn3oonhJqScWYBHzKNfjTmMdfv3YeDCHfgm7Bjah17A8I1R+CE6HnEvUhg6TWKdFf3lp6eRd5vxIa7r15HD"
            + "UHB1El6dGI/4bQMQu7gjYua2wN35LZG2oy9jFLN4H7wf5jBwbjVS5wzAxVouOM26iGjGxfe7+WCAvhHcVH09MtqlGoYkA2KmUxZ/"
            + "xuZIBtomavyDP9a/STBjL4Gm2tf7m/kVItdlAH3R/6Btylwb6zDIc7HnHA0VGNLCGRGb+qE4NpT+PtfLpcHUYyORd3Ey3pwORtYJ"
            + "wYtZKL67jvL7F+J4GSUvkpDwIBWb99/EyEVn0Sf0FAYtPY+wPbfxy7WnSGBd0PvsByjOjELJM9qtT7h3np7n8eQD9W5J4hnKjnXI"
            + "PsMYayRzJJE7sWPfdkxZvhmD5h9A3/m/IXjzafx8+QqevLiLghzaUa/pDyf/goLYZXh1ahjt/4G81gnIPzIJz9YNQcLcHng6tztS"
            + "Qrsibf1AvD9Nu+jyfOoK+s+HFyJuem/8Wtcd+13scdDVAyuZa+qirw8XmTIXJqNvpsHeQA3RTyl6mIRvUGb9m5tYSfIn8WP9K9mf"
            + "1WgbfRp/5R6Qc+/oEHuF6OHlOU1Z+2DC150MtTCgmRNOrvkWmeeZ3zo5jGZeD9rQ/RnbGsP80mgUnh/CtcScxw3aQw+4lh5to31/"
            + "DE/uR+D4mQvYsPcMlu06i9UHLmD3uWjq5Kt4lhqBvBdHuFeEzU6+Ja4ibSbto3KgHrn7A4oZ2y4gb7POzkfC+fU489s27NizC6u+"
            + "P4rl209j+8GTuHj5N6Q+OorCVPIvmbGmu5toW85CwTHm4E8P5rqeCOyegJQZPXGvf2s8G9YRKSM64M6kjrga3gen1vfF8VV9cHrR"
            + "t9jBXqm5Ne0ws7oNZlR3wCC7KmhoqA97ubJvWSFTxvo15AqpDkHMcpGX1ghp0/50UuvfoeX9r5r0DX4Pf9FPraVlRDmkz/i3HqxY"
            + "G2zBeiQHrv/u9e2xJ6wLbu8ZjetbuyJ2Yws8PdAJhZcG0JamLXexK3CpK3nQjfZGP8YTuOdjFiM5ZhtuxR7FlRvRiLpzE5fvXcfV"
            + "uEu48+AIku9TRt9bhLe3mbu6OQEF18cx9zgVeVdCkRu1GPkX56HkDGtITk+lbuceiFiDxzH7EH/rIm7evI3YG/dx++YVxF8/yHzb"
            + "Rry9sRoF15ajmPYOqG9xjHbmudF8PhX5a4fjepdG+M3PCVfq+SKmqT92NXTDpAYu+LqhM76u74SeQc7o6GqNJoxBNLE0RENLY/iz"
            + "T8mRfRpWEv7KmUQKqR5Iib1Mwl+QQqrN8fOrpbQ/H37kf0nxhzacMVUWfzWpa6/ljOvpsO5L1DlqMe5kZmgEE8o3Gz0FOta2x5YZ"
            + "3+DS1lEUk11xdlkLPNjZHnln+tCW7qPEP7obY43fIOdUF+qCIZS/k5F8aQXrEo/hdeYDZOekUUY/QWpGLB492IeHVxcgOWIM/eZB"
            + "eEc+FkQNYryUdTuM2709RczpU5dwvyGC9u1F5hSiViP74REUZ91nbeNzFLx/Td3xABn3DzAMtxgZEXNo88xCPo8rYiwUtPdxhfL9"
            + "7FxkLx+KE408sZHxtgOVK+CwsxVmVzJAgKZG6dyhSiQGzaQ++8qM/1gqPvTqK+shxIwoMa+IsQHVuhe+sHIfKKS5Fa3YYxD/kLPU"
            + "Hn4Uf5Dib0MZf1PjrynldsrsA/E3c+06esZSHaSoc9OifaVJnluxT7ZDXTdsndMfV3bNQtRW9iR+1xePfhqAnNPDiQ/XmdgHsYNR"
            + "Ek0dEUmb+spEZFG3vbi6FnlPKNPznojKZNboMv6ZdRcZCQfx9MpCpFwYyxzyUObbKbuuca3yuGLa4gUX55JCGSsIQ97lULyMmEde"
            + "0qd9yBzZO+rYHMZDCxiTy6aN+2APHkUx9xYxi/o2lDY/ZfqZYF5TCHCT8aCIZXi9ij2XzXyxvqIu9tKuPOBogRmWeqip8QH/iioS"
            + "fctmtPtFXkBblYc3JrbGxNhAzLIR81akGChJS1s530Uml+a3qGpCE4i/f/n48yLGnzWlmV6fwF88p4+hoP8g09STbFFlf5Em7X9D"
            + "9jH6YX3IUJzdPhfnN0/BRfaBxu0Zh6xTwVzzU1BwgXmPqDHIixzBuoOxjBvT52H888WVzch5eAH5z+OR+ypZsnWynsSwDvQgnkVz"
            + "b0TOwOvLE5ETPREF0ZPpQ8/g8awdjAxnTnkF3kZTj15ZgmdRy5DI9Z+esB/vX0Yj+8UtZGfGISs9Ck/v/YhHV0XOczle31iL18xB"
            + "P2c+4PX5ucxFLkD2b/PwJHwUTn5dDzu8KuOguxUOelgipLI+6mgq+5Qri1kJJFvBA8ZhzFlbZsgeHz3uf1P2+VtRL1rSNjRkz5tC"
            + "PV9E2D6Mj0l6WGXbL2JtpTQX8OHHPWLq/Mtn8S+daUQfm9+nb2wOC/NKqFjBAo6V6fsy3zuhT0csGd8by8d0xsox7bCTNYUnVwzE"
            + "xTUDcHZ5Z5xa+hWOLWqFE8s74Oz6/vht9XD8vHoyftu+Gr/9uBOHdu7EwR3bsX/rOuz/LgyH1o/DEX7uxLruOLW6M06u7IQTy7qS"
            + "+uPUilE4sWY0DrNP/ud1g7B/43DsYQ/9od1z8NuhVTi0by1+3rseP+8Kx56twdjLXPqh7yfgKGs/D20ch72scfhpaS/WHfbA8fk9"
            + "cXo6ZeaIr3Cx3xe40rseoroGYGt9Z/SwMEEQZ9E11TXBl4zvB1L2VqNutWKe0Ih14PrM+Zqy9t9KzwKWOuaMAxmo8NdWzpHSUubC"
            + "hG8lcjeHlL2qOz+VfxR2ka62dmmeR6H2BdT4ayhneBmbmsPe2Y29cb5wdfGAu7MLAr3c0aymD1oHeOKrmtRTNR3Qo54rBjT1wcBm"
            + "PuhT3wGda1VEJ78K6FKnIno0dqJOc0P7IH90btYG37TogrZNvkbrRu3RunFrtGvcGN9QB/b4wgW9mlRFr4aV0b2uJboFWqNrIPua"
            + "6nqicz03tKlTBa3qWaMNP9O6mSN7uHzQ6as6+Kp5XbRpVp8UhBaNfdGyqSdaf+mNll94o3ljDzRv6MLvqoaOjWwxuKUTlvatyxrF"
            + "/sj8cRre7qZ+2cbekandsYnx3BBrN8x3qI25jkH41swBteh/VqVMNxE9D7QvdShn9CgThE+kQxtTS5r3pKPsBZAJ7OXS3AXhryZ8"
            + "Pv/YW+BfJ7DOp/FX62HuvYqVbeBRoxZ8/APh6eUPTw8f9oJ6wJu5YC8ba/gzDhdoVwmBVSvzeRXUtLOHH5+7VaSfYqUPXzv2htlb"
            + "orp1RThY2sLNrgZc7euiWuXapEA42NSEUxV3VK9SFZ625vC2NYKvraGSWMvrY20F78pV4FHZEs6V9OFkowWXqlpwrqqD6lWN4Mpj"
            + "XPiei40N/7aFE30lR9qJdnY2sGRssJJdZdi72MLRhXM5bLXh46iFga2r48IG6pebm2gj0869sADvhExqWB87bfzp67bELo/WmGLl"
            + "ikaUvc7UtVaU9ybSjEDGPllzrC3mjdBG1JTmB6pnCGpK8wOF/mjM2gYV/r0/01udMIo1Er+LP+Oe5pWt4erLnnHmC9y8RLy/Bmp4"
            + "+8HfzQv+Toz9O7kgyMUVtRxZj2Xriuq2xNLeFU5V7VHdwRaerg7wYs7M0a4aqlg7wsHOH4729WFv2wAOVRuimm0Qn/vCvooTHJhf"
            + "dmZsr7p9JXg42sDbkTyuVh3uti7wYG2Wn6Mdajpb89EC/vbmqGFjhhrkqz/7z2rZ2qMW+7f8HLhHq3nAzrY68XeGrYs3XGkHVmf+"
            + "zraaiNWaoHtLd5zYRJv0Pn2NuI202ZYgO3wkTtULwh5LHxy1b4qDTs0wy9wBLYlDDZKzpBc0WYvLHAhzYLq6tEtEPljUgBN7MT/T"
            + "kO8LH0nU645nbU+CsifpU/UnBmL+6S7WX5XFX7Os/FE9GlmYsVfXAx41ib8v78Pdl73S/qhdozZqe9UiDzxR09GL+boA1PCoC2fn"
            + "AFR18IajG8ndFY6sTfSo6QtnT09UcXCDg1NN1m01JDWFi3MT5jbrs78+AI5OXnCqXp3kwLVqDxfyzd3dnT3ZlHsOnvC0U/K5oXM1"
            + "NLCvjCZc14247htUtEBT1oA1qWyLRuRTQ0c/+Nn7c5/4oKrgtVtd9hXUhYO7H+WoM/x8ndGnYxD2rR6L55c3sk5lPe3lcLxcMRbH"
            + "GtTDbmsvHOb62M89Ot+0KnpTR7YiLnVILnxuTTljQFkv11TVnciFzBFzS9kHRDLTELOO5KwH+Ungf/BzvRjxyhm0sLKo+Lv4axty"
            + "fk8VG1Sm3Leyd+KaIj5cY76eAfBjDaaXnRd8qvmi7RffYNKEBZg+ew3GsG526rwlmDQvDOPnhmDG0vmYtjAME0LmYOrMRZg2IxzB"
            + "M1ciJGQNZsxchWkzlyB49gIEh4Zi6tyZmDw7GFPmTMcMznWYxd622SFhCB41AeNZqzq2TStMbtMCM9s0x4wWTTC9aQOENG/C580w"
            + "u0MXLB86CcvGz0fYpCWYOS0cQ0fMRvO2vbl/G8LJ1Re1/HzQvlkthAz9Ct+H9sW+sO74NbgjdnZtiFncX9OMLbGogi2WmVTBPMNK"
            + "mMEesEl8HMK6u7amVvA1qoAKOqqZjgrlPEdRmyzmxgrszUkOFlZS3CfhM/WH6tkaoka0O++pLP7ysrE4UffFuUSaRvrQMqEvxtiT"
            + "toEZrKyrwdmR+7qaN9yq+qCmaxBGD5mKy+y9TcnIQ8Kz10jKfIOEzBe4x3h+fOZTxL8QMZ4Uxu5fIvkJKTkTac/eIpX0JCUTT9L5"
            + "GmtJHj1/hkTGjh89f4InL9Pw7AVr39LTERd1GT+HL8XuaeNxfM4UnJ87CRdmj8eFWaNxcfZInJw5AhHh8/D05Cm8uZ+MN8wPZ/Dc"
            + "R45FYtDIqfCu2Rh2Dl7wdPPkvnVGm0AXdKHN83VNa3R2N0V7G300oJ3Pon18RepFmsoZImtsvLCFsxS+c6qHmU610MqqKmwYg9CS"
            + "4j7ESEfUxHF+AfWusSSfFPi2W0+o5vz+Tv1tsqWokRYzaRXlZZCqrlTOGS0yXbkyvqTQlOxRBXtdrCrbM/flA5dqPvBw8EODml8i"
            + "bMZSpD5V9j+WqPoQ3/LZS/7/ouQ1vS3GNqViTr7LftSSghJVU6Tyw+oe7Tz+kcP/89ixWiR1MeZLPa2ZD27gzIYlOBY6nv0c0xl6"
            + "nYqElZPwaPU4PFg1HNGs/7zx3XTkXD9DnyyztDYmgnz7ln6QnbMXLK0o05w9UYN1jrVdqiLIqTJnnlmiro0F55+xzon3as97ry5m"
            + "FZEGc07vRvcA7HVtgF99W2G9TxN0trKDvR5r8DkzQqanij9ri9mkYm6vXJqdvIOYqmYtW/7BjBMxk5k6yUnqJ9Aqg7+Yayrmg4hZ"
            + "XWJOl9om1Wb9eRXKIVcPf1R38oO7oz8aBrTA4rkr6QN93NMp+j/Z+YzXJa/wpiSTSGZLr6KQfMjNVTbtCkYVlajHNRDxYqKdx8+y"
            + "FkjZNcz3X+EV40QXN4bh3PwRrAni/KwFAxG3qC8ervgW91d0QQR7m65wDtM71s2iMF3VgVqAC5dPodegPpINbUv7oKZPIPtsOJum"
            + "ujMChS1NftRx9YYna5vMjXWkWhPR9yV8sK60pdYE1MWeGo3xW+122OzfHF0sbGFH2a+vLVPGnwX+Osr+O9G35+b45/ovVPiLudgJ"
            + "U9l/JHxr7bJySMybZV5HwXiPpr6mUtaJz3B+nU01R0kPO1Av2tu4wte1NiYOD8at6Nt4kf4CTxJT8fBxMjLeZEprOYcY5nL9Fwj8"
            + "S5joYqwGb1hvlVugbMLOL0YJm4OLVDzLIfpi/YtnbBZlfCENr26fxaXNs3Fh8SDW2w5A1OxOuMYa07vsLbu5vDl7jergwlrWwN9h"
            + "nWLhE/Ium8dn4WzUUfQb2RcevjVYO1aT8yUac54HbTjW8fo6u3OuB3WYWyCq2DlCm/hrCEy1lPHlL2wqIZh1f0s9A7GxRhOEedZF"
            + "K+qAysRZR678nCSD+ChqRIXdL7BM+JP9R6rZYhvEPGRpxruY9Sj6HDVkpbaoprbYA3LlzDQRc6UfblbJinvAgTVBtjA1skQVi2rM"
            + "SbbD/MkLsG7BOiyds5x1uRsRcTUKWQXviHs+Zct70Q3Ntcn1/Jb4s08YOYXKwQS5JSguVPbE54l6On5SHFFSwjfzRcP8M5awnMDF"
            + "76bg9OJeuBbeE5fnfcU+u9a4u6otrq9thqPzA3ByXWfWue/i9kokL1/wPBk4Gf0L+o/pC08/X7h7+CEooD68XV1RlX6FdUUb1hW7"
            + "ooKlK2cwWTGPolDmU/REvZUG3CsYoQ19mW7WVdGftmxXG0f46xvDQqbEX6aquRX5cYG9KX2uWPbfJfzJ/jsV/mIOeOYgzkPWlmbM"
            + "aqpy+6oaPLEHuP7lggfS7GXm/jmvzpgzN4yMmZvhzDFD5nlcrKujkWcjNPVugiDPILRp2QEbt21DyuvnhJizR/h/CXmAQlIOefBe"
            + "yCDVYAixzFVbQYm/kBzUESXiRe4Z9q9n3vgNZ9aPxrGF3+Da6h6IDe+E26s74f7mTrixpTV+W9YAJzf3Qsqd/SjKZQ8299x7ZOIY"
            + "8e89vBccmT+sVtUFNb0pNylrzDgHUYe6TEu/MmWILckMGqx1lrHeX2FO+cKcYkXW2ThwRpkb4w/ejD24s/ewCuNrpnJlD4y8TK2V"
            + "kB1D2H+aoOzB7vQX8Be+wM4IzqLWlSlnwAv8hSzTon2rpc16I64LLUPGuXU/1LlrUS7p6NH/E32R9L+taKd5WrnB3dwFtsZV2JPk"
            + "jwXLw2kHpROHfJU+FUAT+4Ic5WAYATjxF8u8oEjaBoJDEuVJarlI+flcyp+bR3Fm7QgcYY1aVHhn9tp9jdjlHRHLfrLLa1oR/y85"
            + "P24U0uJYb1GYq7IDinDh+nkMGjUELvQPHSo5IdCjBrwcq8LCSNlTq8H8qoYue4D0OevBlHnDisTAnBjzns01xVxXOeOgMoksSGYk"
            + "E+Kup5oTru7/EjU6Yua4au67wV+cb9hF7IHhQ0ZK+GtJM2epz0WvB/OaCtoFOkZ83UBTKfPkypl4og5IIfrzeIy1kRXqVg9A/ep1"
            + "4G7jxhlWdRG+cSMy8rIlecLpLygooU4togwqIMIFXNsFygUvZkRIszNUMzOyVTwokAYokVf5Gci6e4zyZyyOhrbDBfasRIR1xPlZ"
            + "bXAq5EscD2uOXxe2Zzx2Fp7ejEVBdkmpHRZz5zamTg0h7nXhU8ULjekzBjrbo7KxrnJGn6gDYR+nhqjHETW2umJOK3FnPLMKc7g2"
            + "jP9U4vqqqCIL+rqm0m8UqGx1lR4YPGKYiPVnkv7yLCBiz5n9yZtu3b4PG/5egYhjaHFt6LHeTcF+J00R+yb22kZ8rieT5J5cU8UD"
            + "vq9Df9Cafkugky/qONdkDwDjEoH1sGbbVrzi4haS562wafi8RAzgKCKmnMsj2aH5YiYV8VbNLKFmoNaUrB5JBkkbJC8dbx6cxY2f"
            + "wjgrdBBi1g3FHcZvbq5h3+nKQYheP1SK55zfuRJ3LsTg/vXniLudinv3knHk6Dn2U0yFv0MA3M1cUM/RE/5VqUMNtJT9v3JlDYlM"
            + "zBfRM5TiCvqUqWaMKQv8q3ButCV5YMFYc0WZAfeDjmTjCztT1P9I87ttOH+D9anEfhPp7/otAfE7QkJvzwtbpMypCVtfrpzJJfSw"
            + "nHpYS18u7QWZqs9A6B0FfQ8d5mpMOHeyikkl2HAfmFGWOjhWx5LVqzn/RVhAxL8kjxpVaROiOJ/7gOgW0e4RerdQOZtGLNtsKPfB"
            + "e85OyivmHCsx4CSfdhRrIp5d+RmPz2xG2oXvWUuyB9mX9yEzai/zCnuZj9mPmGMHsH/LPiycswXTp6zCzBnLMYq+V9P6rVDZsArn"
            + "wFaAI/WsPWW/Gf0m7dK+Ldob2mI2sbE0S8GIOScjxjvNNATpMK7DmAP5YcycuPRc+q2UD/iHLlggsE8om+f6O2ceSn0xTRo3/VAX"
            + "VKbPT/2bB2XnZEq8EX3a1ANi3egyPivm8RsamSB0PuOKhcKjKkFOkbAsiyWpUFJcwOWfJ80DEy8V0fZ8Txs0h/xQe1zvKZ/e5eVQ"
            + "THGvFOdQp75C/ssk5L1MZF3JI5Q8ZwEj8zcl2axLfJPMHGQmHt29iZApIfD3acAYny+8PWvDnXUjFmbWvC7W04icFXPYxpQvhrTn"
            + "tDXV8RaZMo4j6sv5vpaINXMN6qlmrmtJNqF4VEg2itpGFPiUzl96+P83f0mFv6vwiaX5Y5+YB/FHJCtTT2dgYIjFi5f8zfzBoqIi"
            + "5LN2s4D4Fxcpp7SVcK3nci5enuCHSm7nc1Pk5HLvkAe5Eh/yVB6aerAb90Xxx0MOn2ekY8qkKaw7qAYTzmQRc6FsbGx5LUYfz/ES"
            + "vhJljqb8Q513+dmfcskGl6lqDGWl9yZT4f7PmD9Wfv7eX8ZfNa9P+IKVGRMODw+nbCn8ABkxfsd5cDmcx5afny+RNKtJxB74OfG3"
            + "4E8JXxOvi+cF3Afv37+XqID8KRZ+Gt/LzWWMIo/Hl5SUnv9pSgrmzp0r9WY6M8bg48P4iIcH/RQrZf+Kxoe8kka5uYKf6rX9V8/f"
            + "K9+b/bne+M/N6tDS1iqdF2Fvb4cpU6bgyhXWhMTHI4HzmO7dvYfbnNGUmJgo9QLcvXsXT548IY55qn2RT1xzySfiWlRUujfEe69f"
            + "v8b9Bw9Y+3mXNl4iZ50l4sGDeOW5E5TnO336NEaNGgVvb2/p+11o8ztydqy5uZl0XZ+9btU1l53p+D81f7LMb1tIv6/wez2qn7oP"
            + "Za+GQupbEuuwW7duGDRoEAYPHsy+pQHo168f6wKGSNS3b1/2SC2SeCTwVa93sRfEc7EH1Ov/GntgFlDPjRkzBsHBwRJvR4wYIZ2v"
            + "f//+GDp0qPRdok/QwsJC6hsU/bLiOsTa/3hu9cdzlAX2fwX/f/b81TI92ifETCcxc/fPyB71jNyyfdtl9/in7q8Jf/Nj9+7d7AXL"
            + "KJUjAn81H8R+ePXqFfbv3y99VsgSMXtCrGuBbXn+f65/vOzs5PKv/xX586+YP1xuRsc1USshZk//2X0g1pK+vv6fWk/NmjXDr7/+"
            + "KumF0hnBxFxQiUq2i+cHDx7kLOQg6lIDqR9Z9CJXqFDhd3v3Pydn1DMtys7v/TPY/yvnb5fhgfgd0jgxK0LMPvkz+0Ddt62c2ySX"
            + "ZnaI58pZ0bKPZhp89dVXnIt9XNLJv/fvzJkzaNeuHeW6PTyZyxQ9saInX5y77O8RlF/PZdd8WflY/jcM/gj//4n582V40E/UcYnv"
            + "/719UPZ+BS7qtWZoaCjhr75HNR/EY926dWmnLsaxY8cQFRXFvvpIXLp0CTExMZJ+FvpavLZixQpprzg4OEj9sLa2ttL6F9+jntOr"
            + "nlVUfl1/isrPt/vkb0r8G/z+QjkexIn99yl9oN7f6n1fVheUndct/hZ8Ud+zmCfRsGFDtGnDuX2tWkkYf/HFF2jOWX6tW7eWXm/a"
            + "tKk0/0D0wQo+Cn4KOST2mJhNIXSteE2Qej/8WXtZzbPPyf9/h98fKSeLrgn9U94uKj8v6HN6WegFtW4Q76nne6hlVXlZ8ClM1MeK"
            + "84hjBS/Uc+rU+67szHz13lD//Tkq/13/Tr+/U04nn/icf1BeBpR/T71Gy/KsrG+q/lx5GaHGsezn1b+/UZZvH8/s0vzomE/x4E/Y"
            + "9/82vz/12d9fKxOr+D1borxN+Hf51mXO/6nvUu+335P9v3eN/+6/v/bp3x88I8Wh/iqm/24k7uE/4fcHPxEvOqreC5+bJ/fvTOKa"
            + "y6z5/4jf3/z0788mKX9/lvU2fzWW9T9B4hrFtf4n//7sJ35DYJOYaynsBjEP+e+JY/+zSVyTuDaVbZOpuub/2N9f/sRvCXRRzQHP"
            + "FPOQxXzjz83Y/VeSuAZxLaoZzZmqaxTX+r/i98c/MeNSzFbfoNrbELN2hV33r9QR4rvEd4rvVsn3BNU1dYr/i3UK/8G8aKCaNX1U"
            + "te4kf17MvxQ+zj9SRolziXOKc6tiBuq1flR1DQ00/o/+U82cbqOy7Q6q94Ug4eMv5Lw0oQ9bcW6UH/uThe8vYi/K+d+y0tieeE28"
            + "Jz4jPiuOEceq4gRl1/lB1XeJ77TU+O+/8vJJ+HG9VXbHTqlXUInbmzI4/hG9UR1zXnWOuapzBvxfkS//YJ/OX2VHiVgTZyUkTVDJ"
            + "jRAVBateG6r6TAvVMdb/RfC//37v3/8D";

        public static Texture2D CreateDefaultTexture()
        {
            byte[] compressed = Convert.FromBase64String(DeflatedRgbaBase64);
            byte[] rgba = new byte[Width * Height * 4];
            using (MemoryStream ms = new MemoryStream(compressed))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int offset = 0;
                while (offset < rgba.Length)
                {
                    int read = ds.Read(rgba, offset, rgba.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
            }

            Texture2D tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            tex.name = "ToysBadgeMod.DefaultIcon";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.LoadRawTextureData(rgba);
            tex.Apply(false, true);
            return tex;
        }

        public static Texture2D LoadCustomTexture(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return CreateDefaultTexture();
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes != null && bytes.Length > 0)
                {
                    Type imgConv = RuntimeAssemblyService.FindTypeBySimpleName("ImageConversion");
                    if (imgConv != null)
                    {
                        MethodInfo load3 = imgConv.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                        MethodInfo load2 = imgConv.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });
                        MethodInfo load = load3 != null ? load3 : load2;
                        if (load != null)
                        {
                            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            tex.name = "ToysBadgeMod.CustomIcon";
                            tex.wrapMode = TextureWrapMode.Clamp;
                            tex.filterMode = FilterMode.Bilinear;
                            object okObj = load3 != null
                                ? load3.Invoke(null, new object[] { tex, bytes, true })
                                : load2.Invoke(null, new object[] { tex, bytes });
                            if (okObj is bool && (bool)okObj && tex.width > 0 && tex.height > 0)
                            {
                                return tex;
                            }
                            UnityEngine.Object.Destroy(tex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SdkLog.Warn("ToysBadgeMod", "加载自定义图标失败，将回退到默认图标: " + ex.Message);
            }

            return CreateDefaultTexture();
        }
    }
}
