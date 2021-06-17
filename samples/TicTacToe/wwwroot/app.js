var oxo = {
    rand: function () {
        return "?r=" + Math.random();
    },

    model: {
        currentGame: "",
        gameList: [],
        name: "New User"
    },

    ajax: {
        createGame: function (cb) {
            $.post("/Game/CreateGame", cb);
        },

        placeMove: function (coords, cb) {
            $.post("/Game/", function (data) {
                if (cb) {
                    cb(data);
                }
            });
        },
        getGames: function (cb) {
            $.get("/Game/Index/" + oxo.rand(), function (data) {
                if (data) {
                    // games playing
                    data[0].forEach(function (x) {
                        x.waiting = x.state == 0;
                    });
                }
                cb({ currentGames: data[0], availableGames: data[1] });
            });
        },
        getMoves: function (cb) {
            $.get("/Game/GetMoves/" + oxo.model.currentGame + oxo.rand(), cb);
        },
        makeMove: function (x, y, cb) {
            $.post("/Game/MakeMove/" + oxo.model.currentGame + "/?x=" + x + "&y=" + y, cb);
        },
        joinGame: function (gameId, cb) {
            $.post("/Game/Join/" + gameId, function (data) {
                // check we have joined
                oxo.model.currentGame = gameId;
                cb(data);
            });
        },
        setName: function (name, cb) {
            $.post("/Game/SetUser/" + name, function (data) {
                if (cb) {
                    cb(data);
                }
            });
        }
    },

    controllers: {
        refreshGamesList: function () {
            oxo.ajax.getGames(oxo.ui.renderGameList);
        },
        refreshBoard: function () {
            if (oxo.model.currentGame) {
                oxo.ajax.getMoves(function (data) {
                    oxo.ui.renderBoard(data);
                });
            }
        },
        play: function (gameId) {
            oxo.model.currentGame = gameId;
            oxo.controllers.refreshBoard();
            $("#board-placeholder").show("fast");
            $("#games-placeholder").hide("fast");
        },
        move: function (x, y) {
            oxo.ajax.makeMove(x, y, function () {
                oxo.controllers.refreshBoard();
                oxo.controllers.refreshGamesList();
            });
        },
        createGame: function () {
            oxo.ajax.createGame(oxo.controllers.refreshGamesList);
        },
        showJoinDialog: function () {
            $("#join-game-modal").modal();
        },
        joinGame: function () {
            var gameId = $("#join-game-input").val().trim();
            if (!gameId) return;
            $("#join-game-modal").modal('hide');
            oxo.ajax.joinGame(gameId, function (data) {
                $("#join-game-input").val("");
                oxo.controllers.refreshGamesList();
            });
        },
        joinThisGame: function (gameId) {
            if (!gameId) return;
            oxo.ajax.joinGame(gameId, function (data) {
                oxo.controllers.refreshGamesList();
            });
        },
        enterName: function () {
            var name = $("#enter-name-input").val().trim();
            if (!name) return;
            oxo.model.name = name;
            $("#enter-name-modal").modal('hide');
            oxo.ajax.setName(name, function () {
                $("#enter-name-input").val("")
                oxo.controllers.refreshGamesList();
            });
        },
        showInvite: function (gameId) {
            $("#invite-game-id").val(gameId);
            $("#invite-game-link").val(window.location.origin + "/Home/Join/" + gameId);
            $("#invite-game-modal").modal();
        },
        showGames: function () {
            $("#board-placeholder").hide("fast");
            $("#games-placeholder").show("fast");
        }
    },

    ui: {
        renderGameList: function (data) {
            var template = Handlebars.compile($("#games-template").html());
            $("#games-placeholder").html(template(data));
        },
        renderBoard: function (data) {
            var template = Handlebars.compile($("#board-template").html());
            var board = {};
            if (data.summary.yourMove) {
                for (var x = 0; x < 3; x++)
                    for (var y = 0; y < 3; y++)
                        board["x" + x + "y" + y] = '<a href="javascript:void(0);" onclick="oxo.controllers.move(' + x + ', ' + y + ');">MOVE</a>'
            }
            var useO = true;
            data.moves.forEach(function (move) {
                board["x" + move.x + "y" + move.y] = useO ? "O" : "X";
                useO = !useO;
            });
            data.board = board;
            $("#board-placeholder").html(template(data));
        }
    }
}

$(document).ready(function () {
    $("#joinConfirmButton").bind('click', oxo.controllers.joinGame);
    $("#enterNameOk").bind('click', oxo.controllers.enterName);
    $("enter-name-input").keyup(function (event) {
        if (event.keyCode == 13) {
            $("#enterNameOk").click();
        }
    });

    $("#enter-name-modal").modal({
        backdrop: 'static',
        keyboard: false
    });

    setInterval(oxo.controllers.refreshGamesList, 1000);
    setInterval(oxo.controllers.refreshBoard, 1000);
});
