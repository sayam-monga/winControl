import Foundation

/// Android key codes (android.view.KeyEvent) the remote sends. The TV injects them as if pressed on its own remote.
enum Key: Int {
    case home = 3
    case back = 4
    case up = 19
    case down = 20
    case left = 21
    case right = 22
    case center = 23
    case volumeUp = 24
    case volumeDown = 25
    case power = 26
    case enter = 66
    case delete = 67
    case menu = 82
    case playPause = 85
    case rewind = 89
    case fastForward = 90
    case mute = 164
    case settings = 176
    case input = 178
}
