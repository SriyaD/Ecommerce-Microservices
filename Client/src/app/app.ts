import { Component, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { Navbar } from './core/navbar/navbar';
import { Loading } from './core/loading/loading';
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, Navbar, Loading],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  protected readonly title = signal('Client');
}
